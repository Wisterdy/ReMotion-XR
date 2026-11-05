"""Idempotent deployment of the user-approved persistent CPU upload gateway.
Only prints IDs, URL, and hourly price. Never logs Pod environment responses.
"""
import json
import os
import urllib.request
import urllib.error
from pathlib import Path
from registry_access import registry_auth

BASE = "https://rest.runpod.io/v1"
def api(method, path, data=None):
    req=urllib.request.Request(BASE+path,
        data=json.dumps(data).encode() if data is not None else None,
        method=method,headers={"Authorization":"Bearer "+os.environ["RUNPOD_API_KEY"],
                              "Content-Type":"application/json", "User-Agent":"ReMotion-Deployment/1.0"})
    try:
        with urllib.request.urlopen(req,timeout=90) as response:
            return json.load(response)
    except urllib.error.HTTPError as exc:
        # Surface validation errors, but redact every submitted env value first.
        detail = exc.read().decode(errors="replace")
        secrets = [v for k, v in os.environ.items() if v and ("KEY" in k or "TOKEN" in k)]
        secrets += [v for k, v in (data or {}).get("env", {}).items() if "KEY" in k or "TOKEN" in k]
        for value in sorted(secrets, key=len, reverse=True):
            if value: detail = detail.replace(value, "[redacted]")
        raise RuntimeError(f"RunPod {method} {path} returned HTTP {exc.code}: {detail[:1200]}") from None

def main():
    auth_id=registry_auth(os.environ["GATEWAY_IMAGE"])
    volume_id=os.environ["RUNPOD_NETWORK_VOLUME_ID"]
    volume=api("GET","/networkvolumes/"+volume_id)
    pods=api("GET","/pods")
    gateway_name=os.environ.get("GATEWAY_NAME", "remotion-xr-gateway")
    matches=[p for p in pods if p.get("name")==gateway_name]
    if len(matches)>1: raise RuntimeError("Multiple named gateways; refusing ambiguous update")
    endpoint=None
    for line in Path("runpod_endpoint.txt").read_text().splitlines():
        if line.startswith("Serverless URL: "): endpoint=line.split(": ",1)[1]
    if not endpoint: raise RuntimeError("Missing worker deployment output")
    env={"DATA_DIR":"/runpod-volume/data", "REMOTION_REQUIRE_REMOTE":"1",
         "REMOTION_API_KEY":os.environ["REMOTION_API_KEY"],
         "RUNPOD_API_KEY":os.environ["RUNPOD_API_KEY"],
         "REMOTION_WORKER_ENDPOINT_URL":endpoint}
    common={"name":gateway_name, "imageName":os.environ["GATEWAY_IMAGE"],
            "containerRegistryAuthId":auth_id, "env":env, "ports":["8000/http"], "containerDiskInGb":10,
            "volumeMountPath":"/runpod-volume"}
    if matches:
        current=matches[0]
        if current.get("networkVolumeId")!=volume_id:
            raise RuntimeError("Existing gateway uses different storage; refusing to replace it")
        pod=api("POST","/pods/"+current["id"]+"/update",common)
    else:
        # The attached network volume pins placement. v1's explicit location
        # enum omits newer regions (including this existing US-NE-1 volume).
        common.update({"computeType":"CPU", "cloudType":"SECURE", "vcpuCount":2,
                       "networkVolumeId":volume_id,"volumeInGb":0})
        pod=api("POST","/pods",common)
    pod_id=pod["id"]
    info={"pod_id":pod_id,"gateway_url":f"https://{pod_id}-8000.proxy.runpod.net",
          "hourly_cost":pod.get("costPerHr"),"network_volume_id":volume_id}
    Path("gateway.json").write_text(json.dumps(info,indent=2))
    print(json.dumps(info))

if __name__=="__main__": main()
