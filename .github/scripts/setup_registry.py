"""Configure the explicitly authorized GitHub pull-only token on RunPod."""
import base64
import json
import os
import urllib.request
import urllib.error

def request(url, headers, body=None):
    req=urllib.request.Request(url,headers={**headers,'User-Agent':'ReMotion-Deployment/1.0'},
        data=json.dumps(body).encode() if body is not None else None)
    try:
        with urllib.request.urlopen(req,timeout=60) as response: return json.load(response)
    except urllib.error.HTTPError as exc:
        raise RuntimeError('Registry setup HTTP '+str(exc.code)+' at '+url.split('?')[0]) from None

def main():
    token=os.environ['GHCR_PULL_TOKEN'].strip()
    basic=base64.b64encode(('Wisterdy:'+token).encode()).decode()
    grant=request('https://ghcr.io/token?service=ghcr.io&scope=repository:wisterdy/remotion-xr-worker:pull',
                  {'Authorization':'Basic '+basic})
    request('https://ghcr.io/v2/wisterdy/remotion-xr-worker/tags/list',
            {'Authorization':'Bearer '+grant['token']})
    print('Private GHCR worker image pull authentication: PASS')
    headers={'Authorization':'Bearer '+os.environ['RUNPOD_API_KEY'],'Content-Type':'application/json'}
    url='https://rest.runpod.io/v1/containerregistryauth'
    entries=request(url,headers) or []
    if isinstance(entries,dict): entries=entries.get('containerRegistryAuths',entries.get('data',[]))
    found=[entry for entry in entries if entry.get('name')=='remotion-ghcr']
    if len(found)>1: raise RuntimeError('Ambiguous registry credential name')
    if found:
        result=found[0]
    else:
        result=request(url,headers,{'name':'remotion-ghcr','username':'Wisterdy','password':token})
    print('RunPod registry configured: name=remotion-ghcr id='+result['id'])

if __name__=='__main__': main()
