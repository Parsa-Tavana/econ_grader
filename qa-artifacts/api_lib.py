"""Shared API test helpers for QA session. Tokens stored in qa-artifacts/*.json (gitignored)."""
import json, io, os, urllib.request, urllib.error

BASE = "http://localhost:8080"
HERE = os.path.dirname(os.path.abspath(__file__))

def _load(f):
    return json.load(open(os.path.join(HERE, f)))["accessToken"]

def call(method, path, token=None, body=None, raw_body=None, headers=None, expect_json=True):
    url = BASE + path
    data = None
    hdrs = dict(headers or {})
    if body is not None:
        data = json.dumps(body).encode()
        hdrs["Content-Type"] = "application/json"
    elif raw_body is not None:
        data = raw_body
    if token:
        hdrs["Authorization"] = "Bearer " + _load(token)
    req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            payload = r.read().decode("utf-8", "replace")
            status = r.status
    except urllib.error.HTTPError as e:
        payload = e.read().decode("utf-8", "replace")
        status = e.code
    except Exception as e:
        return {"status": 0, "body": str(e)}
    if expect_json and payload:
        try:
            payload = json.loads(payload)
        except json.JSONDecodeError:
            pass
    return {"status": status, "body": payload}

def multipart(fields, files):
    boundary = "----qaboundary7d9f2c"
    buf = io.BytesIO()
    for k, v in fields.items():
        buf.write(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n".encode())
    for k, (fname, content, ctype) in files.items():
        buf.write(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"; filename=\"{fname}\"\r\nContent-Type: {ctype}\r\n\r\n".encode())
        buf.write(content)
        buf.write(b"\r\n")
    buf.write(f"--{boundary}--\r\n".encode())
    return buf.getvalue(), f"multipart/form-data; boundary={boundary}"

ADMIN = "admin_login.json"
TEACHER = "teacher_login.json"
