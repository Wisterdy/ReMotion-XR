"""Resolve existing registry auth or verify anonymous image pull before deployment."""
import json
import os
import urllib.request
import urllib.error

def registry_auth(image):
    req=urllib.request.Request('https://rest.runpod.io/v1/containerregistryauth',headers={
        'Authorization':'Bearer '+os.environ['RUNPOD_API_KEY'],
        'User-Agent':'ReMotion-Deployment/1.0'})
    with urllib.request.urlopen(req,timeout=60) as response: credentials=json.load(response) or []
    if isinstance(credentials,dict):
        credentials=credentials.get('containerRegistryAuths',credentials.get('data',[]))
    matching=[c for c in credentials if 'ghcr' in c.get('name','').lower() or 'github' in c.get('name','').lower()]
    if len(matching)==1: return matching[0]['id']
    if len(credentials)==1: return credentials[0]['id']
    repository,tag=image.removeprefix('ghcr.io/').rsplit(':',1)
    try:
        with urllib.request.urlopen('https://ghcr.io/token?service=ghcr.io&scope=repository:'+repository+':pull',timeout=30) as r:
            token=json.load(r)['token']
        req=urllib.request.Request('https://ghcr.io/v2/'+repository+'/manifests/'+tag,headers={
            'Authorization':'Bearer '+token,'Accept':'application/vnd.oci.image.index.v1+json'})
        with urllib.request.urlopen(req,timeout=30) as r: r.read(1)
        return None
    except urllib.error.HTTPError:
        raise RuntimeError('Private GHCR image cannot be pulled. Configure a GitHub read:packages registry credential on RunPod before deploying.') from None
