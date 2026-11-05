import asyncio
import httpx
import pytest
from app.services import dispatch

@pytest.mark.parametrize('error', [httpx.ConnectError, httpx.ConnectTimeout])
def test_recovers_connection_failure_before_single_accepted_post(monkeypatch, error):
    calls=[]
    def respond(request):
        calls.append(request)
        if len(calls)==1:raise error('connect failed',request=request)
        return httpx.Response(200,json={'id':'one-job'})
    async def no_wait(_):pass
    monkeypatch.setattr(dispatch.asyncio,'sleep',no_wait)
    async def run():
        async with httpx.AsyncClient(transport=httpx.MockTransport(respond)) as client:
            r=await dispatch.provider_request(client,'POST','https://provider.test/run',json={'input':{}})
            assert r.json()['id']=='one-job'
    asyncio.run(run())
    assert len(calls)==2

@pytest.mark.parametrize('error',[httpx.ReadError,httpx.WriteError,httpx.ReadTimeout])
def test_never_resubmits_after_uncertain_transmission(error):
    calls=[]
    def respond(request):
        calls.append(request);raise error('uncertain',request=request)
    async def run():
        async with httpx.AsyncClient(transport=httpx.MockTransport(respond)) as client:
            with pytest.raises(error):
                await dispatch.provider_request(client,'POST','https://provider.test/run',json={'input':{}})
    asyncio.run(run())
    assert len(calls)==1
