#!/usr/bin/env python3
"""Smoke test for a running Paster instance. Standard library only.

    python tools/smoke.py https://paster-sy4ce.azurewebsites.net

It uploads a small file, checks the download headers and bytes, confirms the
byte-size cap is enforced with a clean JSON 413, and reports what the server
says about limits and expiry.
"""

from __future__ import annotations

import hashlib
import json
import sys
import urllib.error
import urllib.request
import uuid

TIMEOUT = 120


def call(request: urllib.request.Request) -> tuple[int, dict, bytes]:
    try:
        with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
            return response.status, dict(response.headers), response.read()
    except urllib.error.HTTPError as error:
        return error.code, dict(error.headers), error.read()


def get(url: str) -> tuple[int, dict, bytes]:
    return call(urllib.request.Request(url, headers={"accept": "*/*"}))


def upload(url: str, filename: str, payload: bytes) -> tuple[int, dict, bytes]:
    boundary = "----paster" + uuid.uuid4().hex
    body = b"".join(
        [
            f"--{boundary}\r\n".encode(),
            f'Content-Disposition: form-data; name="file"; filename="{filename}"\r\n'.encode("utf-8"),
            b"Content-Type: application/octet-stream\r\n\r\n",
            payload,
            b"\r\n",
            f"--{boundary}--\r\n".encode(),
        ]
    )
    request = urllib.request.Request(
        url,
        data=body,
        method="POST",
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
    )
    return call(request)


def report(label: str, ok: bool, detail: str = "") -> bool:
    print(f"{'PASS' if ok else 'FAIL'}  {label}{(' - ' + detail) if detail else ''}")
    return ok


def main() -> int:
    base = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5000").rstrip("/")
    passed = True

    status, _, body = get(f"{base}/api/health")
    health = json.loads(body or b"{}")
    passed &= report("GET /api/health", status == 200 and health.get("status") == "ok", json.dumps(health, ensure_ascii=False))

    status, headers, body = get(f"{base}/")
    passed &= report(
        "GET /",
        status == 200 and "Paster" in body.decode("utf-8", "replace"),
        f"{status}, {len(body)} bytes",
    )

    payload = bytes(range(256)) * 1024  # 256 KiB
    status, _, body = upload(f"{base}/api/upload", "smoke-test.bin", payload)
    created = json.loads(body or b"{}")
    passed &= report(
        "POST /api/upload",
        status == 201 and created.get("size") == len(payload),
        f"{status}, expires in {created.get('expiresInSeconds')}s at {created.get('url')}",
    )

    url = created.get("url") or ""
    if url.startswith("/"):
        url = base + url
    if url:
        status, headers, downloaded = get(url)
        digest = hashlib.sha256(downloaded).hexdigest() == hashlib.sha256(payload).hexdigest()
        passed &= report("GET /d/{token}", status == 200 and digest, f"{status}, {len(downloaded)} bytes, identical={digest}")
        passed &= report(
            "download headers",
            headers.get("Content-Type") == "application/octet-stream"
            and headers.get("Content-Disposition", "").startswith("attachment")
            and headers.get("X-Content-Type-Options") == "nosniff"
            and headers.get("Cache-Control") == "no-store",
            f"disposition={headers.get('Content-Disposition')}",
        )

        token = url.rsplit("/", 1)[-1]
        status, _, body = get(f"{base}/api/status/{token}")
        state = json.loads(body or b"{}")
        passed &= report("GET /api/status/{token}", status == 200 and state.get("exists") is True, json.dumps(state))

    status, _, body = upload(f"{base}/api/upload", "设计稿终稿-v3.png", b"x" * 4096)
    unicode_file = json.loads(body or b"{}")
    passed &= report("unicode filename accepted", status == 201, f"{status}, name={unicode_file.get('name')}")
    if unicode_file.get("url"):
        status, headers, _ = get(unicode_file["url"])
        passed &= report(
            "unicode filename round-trip",
            status == 200 and "%E8%AE%BE%E8%AE%A1" in headers.get("Content-Disposition", ""),
            headers.get("Content-Disposition", ""),
        )

    cap = health.get("maxFileBytes") or 1024 * 1024
    status, _, body = upload(f"{base}/api/upload", "too-big.bin", b"0" * (cap + 4096))
    problem = json.loads(body or b"{}")
    passed &= report(
        "oversized upload rejected cleanly",
        status == 413 and isinstance(problem.get("error"), str),
        f"{status}, error={problem.get('error')}",
    )

    status, _, body = get(f"{base}/d/aaaaaaaaaaaaaaaaaaaaaa")
    passed &= report("unknown token", status == 404, str(status))

    status, _, body = get(f"{base}/api/nope")
    passed &= report("unknown api route returns json", status == 404 and b"error" in body, str(status))

    print("\n" + ("all checks passed" if passed else "some checks FAILED"))
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
