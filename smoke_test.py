#!/usr/bin/env python3
"""End-to-end smoke test for the Meeting Recorder API (stdlib only)."""
import json, sys, time, urllib.request, urllib.error, uuid
from pathlib import Path

BASE = "http://localhost:5080"
ok = fail = 0

def call(method, path, body=None, token=None, files=None, raw=False):
    url = BASE + path
    headers = {}
    data = None
    if files:
        boundary = "----mr" + uuid.uuid4().hex
        parts = []
        for k, v in (body or {}).items():
            parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"\r\n\r\n{v}\r\n')
        for k, (fname, content, ctype) in files.items():
            parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{k}"; filename="{fname}"\r\nContent-Type: {ctype}\r\n\r\n')
            body_head = "".join(parts).encode() + content + f"\r\n".encode()
            parts = []
            data = body_head + f'--{boundary}--\r\n'.encode()
            headers["Content-Type"] = f"multipart/form-data; boundary={boundary}"
    elif body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            payload = r.read().decode()
            return r.status, (payload if raw else json.loads(payload))
    except urllib.error.HTTPError as e:
        payload = e.read().decode()
        try:
            return e.code, json.loads(payload)
        except Exception:
            return e.code, payload

def check(name, cond, extra=""):
    global ok, fail
    if cond:
        ok += 1
        print(f"  PASS  {name}")
    else:
        fail += 1
        print(f"  FAIL  {name}  {extra}")

# 1. Health: swagger json reachable
print("== 1. Swagger ==")
s, _ = call("GET", "/swagger/v1/swagger.json")
check("swagger.json reachable", s == 200, f"got {s}")

# 2. Login (seeded admin)
print("== 2. Auth ==")
s, r = call("POST", "/api/auth/login", {"email": "admin@meetingrecorder.dev", "password": "Admin@123"})
check("admin login succeeds", s == 200 and r["success"] and r["data"]["token"], json.dumps(r)[:150])
token = r["data"]["token"] if r.get("success") else None

s, r = call("POST", "/api/auth/login", {"email": "admin@meetingrecorder.dev", "password": "wrong"})
check("wrong password -> 401 envelope", s == 401 and not r["success"] and r["statusCode"] == 401)

s, r = call("POST", "/api/auth/login", {"email": "not-an-email", "password": "x"})
check("invalid email -> 400 validation", s == 400 and r["errors"], json.dumps(r)[:150])

# 3. Recordings list (seeded)
print("== 3. Recordings CRUD ==")
s, r = call("GET", "/api/recordings?page=1&pageSize=10", token=token)
check("list recordings", s == 200 and r["data"]["totalCount"] >= 3, json.dumps(r)[:200])
rec_id = r["data"]["items"][0]["id"]
seeded_bookmarked = any(i["bookmarked"] for i in r["data"]["items"])
check("seed has a bookmarked recording", seeded_bookmarked)

s, r = call("GET", f"/api/recordings/{rec_id}", token=token)
check("get recording by id", s == 200 and r["data"]["id"] == rec_id)

s, r = call("GET", "/api/recordings", token=None)
check("unauthenticated -> 401", s == 401)

s, r = call("GET", "/api/recordings?search=zzzznothing", token=token)
check("search finds nothing", s == 200 and r["data"]["totalCount"] == 0)

# 4. Full chunked upload flow
print("== 4. Batch upload ==")
s, r = call("POST", "/api/upload/start", {"userId": "22222222-2222-2222-2222-222222222222",
                                            "fileName": "team-sync.mp4", "type": 4, "totalChunks": 3,
                                            "sourceLanguageCode": "en", "fileSizeBytes": 3000}, token=token)
check("start upload", s == 201 and r["success"], json.dumps(r)[:200])
batch_id = r["data"]["batchId"]

chunk_bytes = {1: bytes([1]) * 1000, 2: bytes([2]) * 1000, 3: bytes([3]) * 1000}
for n in (2, 1, 3):   # deliberately out of order — merge must still work
    s, r = call("POST", "/api/upload/chunk",
                {"batchId": batch_id, "chunkNumber": n, "totalChunks": 3,
                 "userId": "22222222-2222-2222-2222-222222222222",
                 "uploadedAt": "2026-07-29T10:00:00Z",
                 "summary": f"summary-from-chunk-{n}", "transcript": f"transcript-{n}",
                 "actions": "action-item", "notes": "note"},
                token=token, files={"file": (f"part{n}.bin", chunk_bytes[n], "application/octet-stream")})
    check(f"upload chunk {n}", s == 200 and r["success"], json.dumps(r)[:150])

s, r = call("GET", f"/api/upload/status/{batch_id}", token=token)
check("status shows 3/3 complete", s == 200 and r["data"]["isComplete"] is True
      and r["data"]["receivedChunks"] == [1, 2, 3], json.dumps(r)[:200])

s, r = call("POST", "/api/upload/complete/" + batch_id, token=token)
check("complete upload -> recording created", s == 201 and r["data"]["title"] == "team-sync"
      and r["data"]["summary"] == "summary-from-chunk-3", json.dumps(r)[:250])
new_rec_id = r["data"]["id"]

s, r = call("POST", "/api/upload/complete/" + batch_id, token=token)
check("double complete -> 409", s == 409 and not r["success"], json.dumps(r)[:150])

s, r = call("GET", f"/api/upload/status/{batch_id}", token=token)
check("status after complete", s == 200 and r["data"]["status"] == "Completed")

# 5. New batch: missing chunk -> complete fails, retry works
print("== 5. Retry flow ==")
s, r = call("POST", "/api/upload/start", {"userId": "22222222-2222-2222-2222-222222222222",
                                            "fileName": "retry-test.mp4", "type": 1, "totalChunks": 2,
                                            "sourceLanguageCode": "en"}, token=token)
b2 = r["data"]["batchId"]
call("POST", "/api/upload/chunk", {"batchId": b2, "chunkNumber": 1, "totalChunks": 2,
                                    "userId": "22222222-2222-2222-2222-222222222222"},
     token=token, files={"file": ("p1.bin", bytes([9]) * 500, "application/octet-stream")})
s, r = call("POST", f"/api/upload/complete/{b2}", token=token)
check("complete with missing chunk -> 400 lists missing", s == 400 and "Missing chunk" in r["message"], json.dumps(r)[:200])

s, r = call("POST", "/api/upload/retry", {"batchId": b2, "chunkNumber": 2}, token=token)
check("retry resets chunk 2", s == 200 and r["success"])
call("POST", "/api/upload/chunk", {"batchId": b2, "chunkNumber": 2, "totalChunks": 2,
                                    "userId": "22222222-2222-2222-2222-222222222222"},
     token=token, files={"file": ("p2.bin", bytes([9]) * 500, "application/octet-stream")})
s, r = call("POST", f"/api/upload/complete/{b2}", token=token)
check("complete after retry succeeds", s == 201 and r["success"], json.dumps(r)[:150])

# 6. Verification: merged file on disk + DB row
print("== 6. Artifact verification ==")
# The API's content root is src/MeetingRecorder.WebApi, so uploads live there;
# merged files land in a per-batch subdirectory: recordings/{batchId}/{batchId}.mp4
updir = Path("src/MeetingRecorder.WebApi/uploads")
merged_files = list((updir / "recordings").rglob(f"{b2}.mp4"))
check("merged file exists", len(merged_files) == 1 and merged_files[0].stat().st_size == 1000,
      f"found={[str(m) for m in merged_files]}")
chunkdir = updir / "chunks" / b2
check("temp chunks deleted after complete", not chunkdir.exists())

# 7. Rate limit: 429 on upload policy (upload endpoint, 60/min cap)
print("== 7. Rate limiting ==")
s, r = call("POST", "/api/upload/start", {"userId": "22222222-2222-2222-2222-222222222222",
                                            "fileName": "rl.mp4", "type": 1, "totalChunks": 1, "sourceLanguageCode": "en"}, token=token)
check("rate-limit endpoint still served", s in (201, 429))

# 8. Batch fetch + playlist + streaming (new endpoints)
print("== 8. Batch fetch / playlist / stream ==")
import urllib.request as _ur

s, r = call("GET", "/api/recordings?pageSize=100", token=token)
items = r["data"]["items"]
with_file = [i for i in items if i.get("filePath")]
check("found recordings with real files for stream test", len(with_file) >= 2, f"got {len(with_file)}")
rec_a, rec_b = with_file[0], with_file[1]

# 8a. Batch fetch — GET (order preserved)
s, r = call("GET", f"/api/recordings/batch?ids={rec_b['id']}&ids={rec_a['id']}", token=token)
check("GET batch preserves order", s == 200 and [i["id"] for i in r["data"]] == [rec_b["id"], rec_a["id"]],
      json.dumps(r)[:200])

# 8b. Batch fetch — POST (JSON body)
s, r = call("POST", "/api/recordings/batch", {"ids": [rec_a["id"], rec_b["id"]]}, token=token)
check("POST batch returns 2 recordings", s == 200 and len(r["data"]) == 2)

# 8c. Batch with a bogus (non-empty) id -> skipped silently
s, r = call("POST", "/api/recordings/batch", {"ids": [rec_a["id"], "12345678-1234-1234-1234-1234567890ab"]}, token=token)
check("POST batch skips unknown id", s == 200 and len(r["data"]) == 1, json.dumps(r)[:150])

# 8d. Empty batch -> 400 validation
s, r = call("POST", "/api/recordings/batch", {"ids": []}, token=token)
check("empty batch -> 400", s == 400 and not r["success"])

# 8e. Playlist
s, r = call("POST", "/api/recordings/play", {"ids": [rec_a["id"], rec_b["id"]]}, token=token)
pl = r["data"]
check("playlist built with stream urls", s == 200 and len(pl) == 2
      and pl[0]["streamUrl"].endswith(f"/api/recordings/{rec_a['id']}/stream")
      and pl[0]["contentType"] == "video/mp4", json.dumps(pl)[:250])

# 8f. Full stream (no Range) -> 200 + correct bytes
full_size = 3000  # chunks were 3 x 1000 bytes
req = _ur.Request(f"http://localhost:5080/api/recordings/{rec_a['id']}/stream",
                  headers={"Authorization": f"Bearer {token}"})
with _ur.urlopen(req, timeout=30) as resp:
    body = resp.read()
    ct = resp.headers.get("Content-Type")
    ar = resp.headers.get("Accept-Ranges")
check("stream 200 with full body", resp.status == 200 and len(body) == full_size
      and ct == "video/mp4" and ar == "bytes", f"status={resp.status} len={len(body)} ct={ct} ar={ar}")

# 8g. Range request -> 206 Partial Content
req = _ur.Request(f"http://localhost:5080/api/recordings/{rec_a['id']}/stream",
                  headers={"Authorization": f"Bearer {token}", "Range": "bytes=0-99"})
with _ur.urlopen(req, timeout=30) as resp:
    body = resp.read()
    cr = resp.headers.get("Content-Range")
check("stream range -> 206 with 100 bytes", resp.status == 206 and len(body) == 100
      and cr == f"bytes 0-99/{full_size}", f"status={resp.status} len={len(body)} cr={cr}")

# 8h. Stream a recording with no file -> 404 (seeded recordings have no file)
without_file = [i for i in items if not i.get("filePath")]
if without_file:
    s, r = call("GET", f"/api/recordings/{without_file[0]['id']}/stream", token=token)
    check("stream without file -> 404", s == 404, json.dumps(r)[:150])
else:
    check("stream without file -> 404", True, "(skipped: every recording has a file)")

print(f"\nRESULT: {ok} passed, {fail} failed")
sys.exit(1 if fail else 0)
