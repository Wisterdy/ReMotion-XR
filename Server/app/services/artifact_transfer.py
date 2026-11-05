"""Bounded transfer for short captures when gateway/worker volumes differ."""
import base64
import hashlib
import io
import zipfile

MAX_BYTES = 6 * 1024 * 1024

def encode(data):
    if len(data) > MAX_BYTES:
        raise ValueError('Cross-region recovery supports artifacts up to 6 MiB')
    return {'data': base64.b64encode(data).decode(), 'sha256': hashlib.sha256(data).hexdigest()}

def decode(payload):
    text = payload['data']
    if not isinstance(text, str) or len(text) > (MAX_BYTES * 4 // 3 + 4):
        raise ValueError('Cross-region artifact exceeds size limit')
    data = base64.b64decode(text, validate=True)
    if len(data) > MAX_BYTES or hashlib.sha256(data).hexdigest() != payload['sha256']:
        raise ValueError('Cross-region artifact integrity check failed')
    return data

def validate_package(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        if 'scene.json' not in archive.namelist() or archive.testzip() is not None:
            raise ValueError('Worker returned an invalid scene package')
