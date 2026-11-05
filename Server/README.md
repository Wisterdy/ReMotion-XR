# ReMotion XR backend

The Python backend receives a recording and its tracking timeline, identifies
visible objects, prepares masks, requests SAM 3D object models, and packages the
result for Quest playback. Human-body reconstruction is not implemented.

New capture reconstruction remains experimental; consult the root README for
current capabilities and limitations. A successful package must contain real
model output before it is presented as a reconstructed scene.

For local development:

```bash
pip install -r requirements.txt
uvicorn app.main:app --reload
```

Model-backed generation requires separately configured GPU services, model
access, and persistent storage. Keep provider and model credentials in the
server environment. Use your own application credential for the client gateway;
do not embed provider secrets in the APK or repository.
