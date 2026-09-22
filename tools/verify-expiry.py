"""End-to-end check that the deployed site really destroys a file after its TTL.

Uploads one file, waits past the advertised expiry, then asserts that the link is
410, the status endpoint says the file is gone, and the bytes are unreachable.
"""

import datetime as dt
import json
import sys
import time
import urllib.error
import urllib.request
import uuid

BASE = sys.argv[1] if len(sys.argv) > 1 else "https://paster-sy4ce.azurewebsites.net"


def call(request):
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


def upload(name, payload):
    boundary = "----paster" + uuid.uuid4().hex
    body = b"".join(
        [
            f"--{boundary}\r\n".encode(),
            f'Content-Disposition: form-data; name="file"; filename="{name}"\r\n'.encode(),
            b"Content-Type: application/octet-stream\r\n\r\n",
            payload,
            b"\r\n",
            f"--{boundary}--\r\n".encode(),
        ]
    )
    request = urllib.request.Request(
        f"{BASE}/api/upload",
        data=body,
        method="POST",
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
    )
    return call(request)


print(f"target: {BASE}", flush=True)
status, body = upload("expiry-probe.txt", b"this file must not survive its ttl\n")
created = json.loads(body)
print(f"upload -> {status} {created['url']}", flush=True)

expires = dt.datetime.fromisoformat(created["expiresAtUtc"])
print(f"server says it expires at {expires.isoformat()} (in {created['expiresInSeconds']}s)", flush=True)

token = created["token"]
status, body = call(urllib.request.Request(f"{BASE}/d/{token}"))
print(f"before expiry: GET /d/token -> {status} ({len(body)} bytes)", flush=True)

while True:
    remaining = (expires - dt.datetime.now(dt.timezone.utc)).total_seconds()
    if remaining <= -5:
        break
    time.sleep(max(1.0, min(remaining + 5, 15.0)))
    print(f"waiting, {remaining:.0f}s left", flush=True)

status, body = call(urllib.request.Request(f"{BASE}/d/{token}"))
gone = status == 410
print(f"{'PASS' if gone else 'FAIL'}  after expiry: GET /d/token -> {status} (expected 410)", flush=True)

status, body = call(urllib.request.Request(f"{BASE}/api/status/{token}"))
state = json.loads(body)
vanished = status == 200 and state.get("exists") is False
print(f"{'PASS' if vanished else 'FAIL'}  after expiry: GET /api/status -> {state}", flush=True)

status, body = call(urllib.request.Request(f"{BASE}/api/health"))
health = json.loads(body)
print(f"health now: {health}", flush=True)

print("expiry verified" if (gone and vanished) else "EXPIRY CHECK FAILED", flush=True)
sys.exit(0 if (gone and vanished) else 1)
