"""Create OR update the RunPod Serverless endpoint 'remotion-xr-worker'.

Reads RUNPOD_API_KEY, RUNPOD_NETWORK_VOLUME_ID, REMOTION_API_KEY and
WORKER_IMAGE from the environment (GitHub Secrets). Secrets are never
printed. Writes the endpoint id + stable URL to runpod_endpoint.txt and
stdout (ids/URLs only, no secrets).

Uses the RunPod GraphQL API (saveTemplate + saveEndpoint). If RunPod
renames a field, the API error is surfaced in the Actions log.
"""

import json
import os
import sys
import uuid
import urllib.request
from registry_access import registry_auth

API = "https://api.runpod.io/graphql"
# Fresh endpoint isolates the repaired handler from the legacy worker release.
# The gateway follows runpod_endpoint.txt; client session IDs do not change.
ENDPOINT_NAME = os.environ.get("ENDPOINT_NAME", "remotion-xr-worker-v3")
# RunPod GPU pool IDs (fallback order): ADA_24 = RTX 4090 class first,
# then AMPERE_24 = RTX 3090 / RTX A5000 class. One comma-separated field.
GPU_FALLBACK = os.environ.get("WORKER_GPU_POOLS", "ADA_24,AMPERE_24")
# A failed deployment can leave its immutable template behind. Retrying the
# same image must use a new template name, then move the stable endpoint.
DEPLOYMENT_SUFFIX = (
    os.environ.get("GITHUB_RUN_ID", uuid.uuid4().hex[:12])
    + "-" + os.environ.get("GITHUB_RUN_ATTEMPT", "1")
)


def gql(query: str, variables: dict) -> dict:
    body = json.dumps({"query": query, "variables": variables}).encode()
    req = urllib.request.Request(
        API, data=body, method="POST",
        headers={"Content-Type": "application/json",
                 "Authorization": f"Bearer {os.environ['RUNPOD_API_KEY']}"},
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        data = json.loads(resp.read())
    if data.get("errors"):
        raise SystemExit(f"RunPod API error: {data['errors']}")
    return data["data"]


SAM3D_ENDPOINT_NAME = os.environ.get("SAM3D_ENDPOINT_NAME", "remotion-xr-sam3d")
# User-selected budget pool: A40/A6000, 48 GB (SAM requires >=32 GB).
# RunPod rejects the obsolete HOPPER_80 pool identifier.
SAM3D_GPU_FALLBACK = os.environ.get("SAM_GPU_POOLS", "AMPERE_48")

def allowed_gpus(default):
    override = os.environ.get("COMPATIBLE_GPU_TYPES")
    return set(override.split("|")) if override else default


def compatible_gpu_selection(pools: str, allowed: set[str]) -> str:
    """Exclude newly added Blackwell/MIG types from our CUDA 12.1 pools."""
    req = urllib.request.Request(
        "https://api.runpod.io/v2/catalog/gpus",
        headers={"Authorization": "Bearer " + os.environ["RUNPOD_API_KEY"]})
    with urllib.request.urlopen(req, timeout=60) as response:
        catalog = json.load(response)["gpus"]
    selected = [g["id"] for g in catalog if g.get("pool") in pools.split(",")]
    if not set(selected).intersection(allowed):
        raise RuntimeError("No known CUDA-compatible GPU types found in selected pools")
    excluded = sorted(set(selected) - allowed)
    print("Compatible GPU selection:", sorted(set(selected).intersection(allowed)))
    return ",".join([pools] + ["-" + gpu for gpu in excluded])


def ensure_sam3d_endpoint(endpoints: list, volume_id: str):
    """Create/move the SAM 3D endpoint when HF_TOKEN + image exist.

    Returns its serverless URL (wired into the main worker template),
    or None when SAM 3D is not provisioned yet.
    """
    hf_token = os.environ.get("HF_TOKEN", "")
    image = os.environ.get("SAM3D_IMAGE", "")
    if not hf_token or not image:
        print("SAM 3D endpoint skipped: HF_TOKEN/SAM3D_IMAGE not set.")
        return None
    auth_id = registry_auth(image)
    short = image.rsplit(":", 1)[-1][:12]
    template = gql(
        """mutation($input: SaveTemplateInput!) {
             saveTemplate(input: $input) { id }
           }""",
        {"input": {
            "name": f"{SAM3D_ENDPOINT_NAME}-{short}-{DEPLOYMENT_SUFFIX}",
            "imageName": image,
            "containerRegistryAuthId": auth_id,
            "dockerArgs": "",
            "isServerless": True,
            "containerDiskInGb": 40,
            "volumeInGb": 0,
            "env": [
                {"key": "HF_TOKEN", "value": hf_token},
                {"key": "REMOTION_SAM3D_DIR", "value": "/runpod-volume/sam3d"},
            ],
        }},
    )["saveTemplate"]["id"]
    existing = next((e for e in endpoints if e["name"] == SAM3D_ENDPOINT_NAME), None)
    endpoint = gql(
        """mutation($input: EndpointInput!) {
             saveEndpoint(input: $input) { id name }
           }""",
        {"input": {
            "id": existing["id"] if existing else "",
            "name": SAM3D_ENDPOINT_NAME,
            "templateId": template,
            "gpuIds": compatible_gpu_selection(SAM3D_GPU_FALLBACK, allowed_gpus({"NVIDIA A40", "NVIDIA RTX A6000"})),
            "networkVolumeId": volume_id,
            "locations": "",
            "idleTimeout": 10,
            "scalerType": "QUEUE_DELAY",
            "scalerValue": 4,
            "workersMin": 0,
            "workersMax": 1,
        }},
    )["saveEndpoint"]
    if existing:
        gql(
            """mutation($input: UpdateEndpointTemplateInput!) {
                 updateEndpointTemplate(input: $input) { id templateId }
               }""",
            {"input": {"endpointId": endpoint["id"], "templateId": template}},
        )
        current = next(
            (e for e in gql(
                """query { myself { endpoints { id templateId } } }""", {}
            )["myself"]["endpoints"] if e["id"] == endpoint["id"]),
            None,
        )
        if current is None or current["templateId"] != template:
            raise SystemExit(
                f"SAM 3D endpoint {endpoint['id']} did not move to template {template}")
    print(f"SAM 3D endpoint ID: {endpoint['id']}")
    return f"https://api.runpod.ai/v2/{endpoint['id']}"


def main() -> int:
    image = os.environ["WORKER_IMAGE"]
    auth_id = registry_auth(image)
    volume_id = os.environ["RUNPOD_NETWORK_VOLUME_ID"]

    existing = gql(
        """query { myself { endpoints { id name templateId } } }""", {}
    )["myself"]["endpoints"]
    sam3d_url = ensure_sam3d_endpoint(existing, volume_id)

    main_env = [
        {"key": "DATA_DIR", "value": "/runpod-volume/data"},
        {"key": "REMOTION_API_KEY", "value": os.environ["REMOTION_API_KEY"]},
    ]
    if sam3d_url:
        main_env.append({"key": "REMOTION_SAM3D_ENDPOINT_URL", "value": sam3d_url})
        main_env.append({"key": "RUNPOD_API_KEY", "value": os.environ["RUNPOD_API_KEY"]})
        main_env.append({"key": "MODEL_API_KEY", "value": os.environ.get("MODEL_API_KEY", "")})

    # Template names must be unique on RunPod: suffix with the image tag.
    tag = image.rsplit(":", 1)[-1][:12] if ":" in image else "latest"
    template = gql(
        """mutation($input: SaveTemplateInput!) {
             saveTemplate(input: $input) { id }
           }""",
        {"input": {
            "name": f"{ENDPOINT_NAME}-{tag}-{DEPLOYMENT_SUFFIX}",
            "imageName": image,
            "containerRegistryAuthId": auth_id,
            "dockerArgs": "",
            "isServerless": True,
            "containerDiskInGb": 20,
            "volumeInGb": 0,
            "env": main_env,
        }},
    )["saveTemplate"]["id"]

    endpoint_id = next((e["id"] for e in existing if e["name"] == ENDPOINT_NAME), "")

    endpoint = gql(
        """mutation($input: EndpointInput!) {
             saveEndpoint(input: $input) { id name }
           }""",
        {"input": {
            "id": endpoint_id,
            "name": ENDPOINT_NAME,
            "templateId": template,
            "gpuIds": compatible_gpu_selection(GPU_FALLBACK, allowed_gpus({"NVIDIA GeForce RTX 4090", "NVIDIA GeForce RTX 3090", "NVIDIA RTX A5000"})),
            "networkVolumeId": volume_id,
            "locations": "",
            "idleTimeout": 5,
            "scalerType": "QUEUE_DELAY",
            "scalerValue": 4,
            "workersMin": 0,
            "workersMax": 1,
        }},
    )["saveEndpoint"]

    if endpoint_id:
        # saveEndpoint silently ignores templateId on an existing
        # endpoint (verified live: it returns the OLD templateId).
        # Template swaps need the dedicated mutation, then read-back.
        gql(
            """mutation($input: UpdateEndpointTemplateInput!) {
                 updateEndpointTemplate(input: $input) { id templateId }
               }""",
            {"input": {"endpointId": endpoint["id"], "templateId": template}},
        )
        current = next(
            (e for e in gql(
                """query { myself { endpoints { id templateId } } }""", {}
            )["myself"]["endpoints"] if e["id"] == endpoint["id"]),
            None,
        )
        if current is None or current["templateId"] != template:
            raise SystemExit(
                f"Endpoint {endpoint['id']} did not move to template "
                f"{template} (read-back: {current})"
            )

    url = f"https://api.runpod.ai/v2/{endpoint['id']}"
    summary = f"Endpoint ID: {endpoint['id']}\nServerless URL: {url}\n"
    with open("runpod_endpoint.txt", "w", encoding="utf-8") as fh:
        fh.write(summary)
    print(summary)
    return 0


if __name__ == "__main__":
    sys.exit(main())
