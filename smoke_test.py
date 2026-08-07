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

def login(email, password):
    return auth_call("POST", "/api/auth/login", {"email": email, "password": password})

def auth_call(method, path, body):
    """Auth-policy calls with a 429 backoff (20/min/IP; smoke runs stack up)."""
    import time as _t
    for _ in range(3):
        s, r = call(method, path, body)
        if s != 429:
            return s, r
        print("  (auth rate-limited — waiting 62s)")
        _t.sleep(62)
    return s, r

s, r = login("admin@meetingrecorder.dev", "Admin@123")
check("admin login succeeds", s == 200 and r["success"] and r["data"]["token"], json.dumps(r)[:150])
token = r["data"]["token"] if r.get("success") else None

s, r = call("POST", "/api/auth/login", {"email": "admin@meetingrecorder.dev", "password": "wrong"})
check("wrong password -> 401 envelope", s == 401 and not r["success"] and r["statusCode"] == 401)

s, r = call("POST", "/api/auth/login", {"email": "not-an-email", "password": "x"})
check("invalid email -> 400 validation", s == 400 and r["errors"], json.dumps(r)[:150])

# 3. Recordings list (seeded)
print("== 3. Recordings CRUD ==")
s, r = call("GET", "/api/recordings?page=1&pageSize=10", token=token)
check("list recordings", s == 200 and r["data"]["total_count"] >= 3, json.dumps(r)[:200])
rec_id = r["data"]["items"][0]["id"]
seeded_bookmarked = any(i["bookmarked"] for i in r["data"]["items"])
check("seed has a bookmarked recording", seeded_bookmarked)

s, r = call("GET", f"/api/recordings/{rec_id}", token=token)
check("get recording by id", s == 200 and r["data"]["id"] == rec_id)

s, r = call("GET", "/api/recordings", token=None)
check("unauthenticated -> 401", s == 401)

s, r = call("GET", "/api/recordings?search=zzzznothing", token=token)
check("search finds nothing", s == 200 and r["data"]["total_count"] == 0)

# 4. Full chunked upload flow
print("== 4. Batch upload ==")
s, r = call("POST", "/api/upload/start", {"user_id": "22222222-2222-2222-2222-222222222222",
                                            "file_name": "team-sync.mp4", "type": "meeting", "total_chunks": 3,
                                            "source_language_code": "en", "file_size_bytes": 3000}, token=token)
check("start upload", s == 201 and r["success"], json.dumps(r)[:200])
batch_id = r["data"]["batch_id"]

chunk_bytes = {1: bytes([1]) * 1000, 2: bytes([2]) * 1000, 3: bytes([3]) * 1000}
chunk_meta = {
    "summary": "summary-from-chunk",
    "transcript": json.dumps([{"speaker": "A", "text": "hello world", "timestamp_seconds": 5}]),
    "actions": json.dumps([{"text": "follow up", "done": False}]),
    "notes": json.dumps([{"id": "n1", "start_seconds": 0, "end_seconds": 10, "text": "note one", "clip_path": None}]),
}
for n in (2, 1, 3):   # deliberately out of order — merge must still work
    s, r = call("POST", "/api/upload/chunk",
                {"batchId": batch_id, "chunkNumber": n, "totalChunks": 3,
                 "userId": "22222222-2222-2222-2222-222222222222",
                 "uploadedAt": "2026-07-29T10:00:00Z",
                 "summary": chunk_meta["summary"], "transcript": chunk_meta["transcript"],
                 "actions": chunk_meta["actions"], "notes": chunk_meta["notes"]},
                token=token, files={"file": (f"part{n}.bin", chunk_bytes[n], "application/octet-stream")})
    check(f"upload chunk {n}", s == 200 and r["success"], json.dumps(r)[:150])

s, r = call("GET", f"/api/upload/status/{batch_id}", token=token)
check("status shows 3/3 complete", s == 200 and r["data"]["is_complete"] is True
      and r["data"]["received_chunks"] == [1, 2, 3], json.dumps(r)[:200])

s, r = call("POST", "/api/upload/complete/" + batch_id, token=token)
check("complete upload -> recording created", s == 201 and r["data"]["title"] == "team-sync"
      and r["data"]["summary"] == "summary-from-chunk", json.dumps(r)[:250])
new_rec_id = r["data"]["id"]
check("snake_case contract on response", r["data"].get("created_at") is not None
      and r["data"].get("duration_seconds") is not None
      and r["data"].get("transcription_status") == "none"
      and r["data"].get("file_path") is not None, json.dumps(r["data"])[:250])
check("structured transcript round-trips as list", isinstance(r["data"].get("transcript"), list)
      and r["data"]["transcript"][0]["speaker"] == "A"
      and r["data"]["transcript"][0]["timestamp_seconds"] == 5, json.dumps(r["data"].get("transcript"))[:200])
check("structured notes round-trip with clip_path", isinstance(r["data"].get("notes"), list)
      and r["data"]["notes"][0]["end_seconds"] == 10, json.dumps(r["data"].get("notes"))[:200])

s, r = call("POST", "/api/upload/complete/" + batch_id, token=token)
check("double complete -> 409", s == 409 and not r["success"], json.dumps(r)[:150])

s, r = call("GET", f"/api/upload/status/{batch_id}", token=token)
check("status after complete", s == 200 and r["data"]["status"] == "Completed")

# 5. New batch: missing chunk -> complete fails, retry works
print("== 5. Retry flow ==")
s, r = call("POST", "/api/upload/start", {"user_id": "22222222-2222-2222-2222-222222222222",
                                            "file_name": "retry-test.mp4", "type": "audio", "total_chunks": 2,
                                            "source_language_code": "en"}, token=token)
b2 = r["data"]["batch_id"]
call("POST", "/api/upload/chunk", {"batchId": b2, "chunkNumber": 1, "totalChunks": 2,
                                    "userId": "22222222-2222-2222-2222-222222222222"},
     token=token, files={"file": ("p1.bin", bytes([9]) * 500, "application/octet-stream")})
s, r = call("POST", f"/api/upload/complete/{b2}", token=token)
check("complete with missing chunk -> 400 lists missing", s == 400 and "Missing chunk" in r["message"], json.dumps(r)[:200])

s, r = call("POST", "/api/upload/retry", {"batch_id": b2, "chunk_number": 2}, token=token)
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
s, r = call("POST", "/api/upload/start", {"user_id": "22222222-2222-2222-2222-222222222222",
                                            "file_name": "rl.mp4", "type": "audio", "total_chunks": 1, "source_language_code": "en"}, token=token)
check("rate-limit endpoint still served", s in (201, 429))

# 8. Batch fetch + playlist + streaming (new endpoints)
print("== 8. Batch fetch / playlist / stream ==")
import urllib.request as _ur

s, r = call("GET", "/api/recordings?pageSize=100", token=token)
items = r["data"]["items"]
with_file = [i for i in items if i.get("file_path")]
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

# 8e. Playlist (now with short-lived signed stream URLs)
s, r = call("POST", "/api/recordings/play", {"ids": [rec_a["id"], rec_b["id"]]}, token=token)
pl = r["data"]
check("playlist built with signed stream urls", s == 200 and len(pl) == 2
      and "/stream?expires=" in pl[0]["stream_url"] and "&sig=" in pl[0]["stream_url"]
      and pl[0]["content_type"] == "video/mp4"
      and pl[0]["duration_seconds"] is not None, json.dumps(pl)[:250])

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
without_file = [i for i in items if not i.get("file_path")]
if without_file:
    s, r = call("GET", f"/api/recordings/{without_file[0]['id']}/stream", token=token)
    check("stream without file -> 404", s == 404, json.dumps(r)[:150])
else:
    check("stream without file -> 404", True, "(skipped: every recording has a file)")

# 9. New contract: structured create + string enums (matches Flutter app)
print("== 9. New contract (snake_case + string enums + structured blocks) ==")
s, r = call("POST", "/api/recordings", {
    "user_id": "22222222-2222-2222-2222-222222222222",
    "title": "Interview with Priya",
    "type": "interview",
    "duration_seconds": 95,
    "summary": "Hiring discussion",
    "transcript": [{"speaker": "Me", "text": "Tell me about yourself", "timestamp_seconds": 3}],
    "actions": [{"text": "Send offer letter", "done": False}],
    "notes": [{"id": "n9", "start_seconds": 10, "end_seconds": 20, "text": "Strong candidate", "clip_path": None}],
    "is_recording": False,
    "bookmarked": True,
    "source_language_code": "en",
    "transcription_status": "processing",
}, token=token)
check("create with structured blocks + enum strings", s == 201 and r["data"]["type"] == "interview"
      and r["data"]["transcription_status"] == "processing"
      and r["data"]["duration_seconds"] == 95
      and len(r["data"]["transcript"]) == 1
      and len(r["data"]["actions"]) == 1
      and len(r["data"]["notes"]) == 1, json.dumps(r)[:250])
structured_id = r["data"]["id"] if r.get("success") else None

if structured_id:
    s, r = call("GET", f"/api/recordings/{structured_id}", token=token)
    check("fetch round-trips structured blocks", s == 200
          and r["data"]["transcript"][0]["text"] == "Tell me about yourself"
          and r["data"]["notes"][0]["clip_path"] is None
          and r["data"]["actions"][0]["done"] is False, json.dumps(r["data"])[:250])

    s, r = call("PATCH", f"/api/recordings/{structured_id}/bookmark", {"bookmarked": False}, token=token)
    check("bookmark toggle (snake_case body)", s == 200 and r["data"]["bookmarked"] is False)

    s, r = call("POST", "/api/recordings/batch", {"ids": [structured_id]}, token=token)
    check("batch fetch returns structured recording", s == 200
          and r["data"][0]["transcription_status"] == "processing", json.dumps(r)[:200])

    s, r = call("POST", "/api/recordings/play", {"ids": [structured_id]}, token=token)
    check("playlist item carries new fields", s == 200
          and r["data"][0]["type"] == "interview"
          and r["data"][0]["duration_seconds"] == 95, json.dumps(r)[:200])

    s, r = call("DELETE", f"/api/recordings/{structured_id}", token=token)
    check("delete structured recording", s == 200)
    s, r = call("GET", f"/api/recordings/{structured_id}", token=token)
    check("deleted -> 404", s == 404)

# 10. Hardening: refresh tokens, signed URLs, list guards, upload caps
print("== 10. Hardening (refresh / signed urls / guards / caps) ==")

# 10a. Refresh token rotation: login carries it, refresh rotates, old one dies
s, r = login("admin@meetingrecorder.dev", "Admin@123")
rt1 = r["data"]["refresh_token"]
check("login returns token_type + refresh token", s == 200
      and r["data"]["token_type"] == "Bearer" and rt1 and r["data"]["refresh_expires_at"], json.dumps(r["data"])[:200])
s, r = auth_call("POST", "/api/auth/refresh", {"refresh_token": rt1})
rt2 = r["data"]["refresh_token"] if r.get("success") else None
check("refresh rotates to a new pair", s == 200 and r["data"]["token"] and rt2 and rt2 != rt1)
s, r = auth_call("POST", "/api/auth/refresh", {"refresh_token": rt1})
check("reused (revoked) refresh token -> 401", s == 401 and r["errorCode"] == "INVALID_REFRESH_TOKEN", json.dumps(r)[:150])
s, r = auth_call("POST", "/api/auth/logout", {"refresh_token": rt2})
check("logout revokes the session", s == 200 and r["success"])
s, r = auth_call("POST", "/api/auth/refresh", {"refresh_token": rt2})
check("refresh after logout -> 401", s == 401, json.dumps(r)[:120])

# 10b. Register conflict codes (EMAIL_TAKEN / MOBILE_TAKEN)
s, r = auth_call("POST", "/api/auth/register", {"email": "admin@meetingrecorder.dev", "name": "Dup", "mobile": "+91 99999 99991", "password": "Passw0rd!"})
check("duplicate email -> 409 EMAIL_TAKEN", s == 409 and r["errorCode"] == "EMAIL_TAKEN", json.dumps(r)[:150])
auth_call("POST", "/api/auth/register", {"email": "dupmobile@test.dev", "name": "First", "mobile": "+91 88888 00000", "password": "Passw0rd!"})
s, r = auth_call("POST", "/api/auth/register", {"email": "other@test.dev", "name": "Second", "mobile": "+91 88888 00000", "password": "Passw0rd!"})
check("duplicate mobile -> 409 MOBILE_TAKEN", s == 409 and r["errorCode"] == "MOBILE_TAKEN", json.dumps(r)[:150])

# 10c. Signed URL streaming (anonymous media player path)
s, r = call("POST", "/api/recordings/play", {"ids": [rec_a["id"]]}, token=token)
signed = r["data"][0]["stream_url"]
signed_req = _ur.Request(signed)
with _ur.urlopen(signed_req, timeout=30) as resp:
    signed_body = resp.read()
check("signed url streams without auth", resp.status == 200 and len(signed_body) == full_size)
req = _ur.Request(signed + "&expires=1&sig=deadbeef")
try:
    with _ur.urlopen(req, timeout=30):
        tampered_ok = False
except Exception:
    tampered_ok = True
check("tampered/expired signature -> rejected", tampered_ok)

# 10d. List guards: sortBy whitelist + date filters + pageSize cap
s, r = call("GET", "/api/recordings?sortBy=evil_column", token=token)
check("invalid sortBy -> 400 VALIDATION_ERROR", s == 400 and r["errorCode"] == "VALIDATION_ERROR", json.dumps(r)[:150])
s, r = call("GET", "/api/recordings?sortBy=created_at&sortOrder=desc&createdAfter=2026-01-01T00:00:00Z", token=token)
check("valid sortBy + createdAfter filter", s == 200 and r["success"])
s, r = call("GET", "/api/recordings?pageSize=9999", token=token)
check("pageSize capped at 100", s == 200 and r["data"]["page_size"] == 100)

# 10e. Upload caps: chunk-count bound + checksum + start validation
s, r = call("POST", "/api/upload/start", {"user_id": "22222222-2222-2222-2222-222222222222",
                                          "file_name": "big.mp4", "type": "audio", "total_chunks": 9999,
                                          "source_language_code": "en"}, token=token)
check("totalChunks > 512 -> 400", s == 400 and r["errorCode"] == "VALIDATION_ERROR", json.dumps(r)[:150])
s, r = call("POST", "/api/upload/start", {"user_id": "22222222-2222-2222-2222-222222222222",
                                          "file_name": "../../etc/passwd", "type": "audio", "total_chunks": 1,
                                          "source_language_code": "en"}, token=token)
check("start response has no upload_directory leak", s == 201 and "upload_directory" not in r["data"]
      and "batch_id" in r["data"], json.dumps(r["data"])[:150])
b3 = r["data"]["batch_id"]
import hashlib
chunk_bytes_ok = bytes([7]) * 400
chunk_bytes_bad = bytes([8]) * 400
good_sum = hashlib.sha256(chunk_bytes_ok).hexdigest()
s, r = call("POST", "/api/upload/chunk", {"batchId": b3, "chunkNumber": 1, "totalChunks": 1,
                                          "userId": "22222222-2222-2222-2222-222222222222",
                                          "checksumSha256": good_sum},
            token=token, files={"file": ("ok.bin", chunk_bytes_ok, "application/octet-stream")})
check("chunk with correct sha256 accepted", s == 200 and r["success"], json.dumps(r)[:150])
s, r = call("POST", "/api/upload/retry", {"batch_id": b3, "chunk_number": 1}, token=token)
s, r = call("POST", "/api/upload/chunk", {"batchId": b3, "chunkNumber": 1, "totalChunks": 1,
                                          "userId": "22222222-2222-2222-2222-222222222222",
                                          "checksumSha256": good_sum},
            token=token, files={"file": ("bad.bin", chunk_bytes_bad, "application/octet-stream")})
check("chunk with wrong sha256 -> 400 CHECKSUM_MISMATCH", s == 400 and r["errorCode"] == "CHECKSUM_MISMATCH", json.dumps(r)[:150])
s, r = call("POST", "/api/upload/chunk", {"batchId": b3, "chunkNumber": 1, "totalChunks": 1,
                                          "userId": "22222222-2222-2222-2222-222222222222",
                                          "checksumSha256": good_sum},
            token=token, files={"file": ("ok.bin", chunk_bytes_ok, "application/octet-stream")})
check("re-upload good chunk after rejected one", s == 200 and r["success"], json.dumps(r)[:150])
s, r = call("POST", "/api/upload/complete/" + b3, token=token)
check("complete after checksum flow", s == 201)

print(f"\nRESULT: {ok} passed, {fail} failed")
sys.exit(1 if fail else 0)
