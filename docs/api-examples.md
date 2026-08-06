# Meeting Recorder API — Sample Requests & Responses

Compiled from the live deployed spec (`https://meetings-api.idealake.com/swagger`, fetched 2026-08-06) with real responses captured against the live API where noted.

**Conventions:**  
- Auth: `Authorization: Bearer <token>` required on everything except `login`/`register`.  
- JSON is **snake_case**; enums are **strings** (`meeting`, `voice_note`, `completed` …).  
- Every response is an envelope: `{ success, message, data, statusCode }`; errors: `{ success:false, message, data:null, statusCode }`.

---
## 🔐 Auth

### 1. POST /api/Auth/login  *(public)*
```json
// Request
{
  "email": "admin@meetingrecorder.dev",
  "password": "Admin@123"
}

// Response 200 (real, live)
{
  "success": true,
  "message": "Login successful.",
  "data": {
    "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0…",
    "expires_at": "2026-08-06T10:18:14.736714Z",
    "user": {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "email": "admin@meetingrecorder.dev",
      "name": "Admin",
      "mobile": "+91 90000 00000",
      "profile_photo_url": null,
      "created_date": "2026-07-21T09:44:17.1441621",
      "updated_date": null,
      "role": "Admin"
    }
  },
  "status_code": 200
}

| Failure | Status | Body |
|---|---|---|
| Bad credentials | 401 | `{"success":false,"message":"Invalid email or password.","data":null,"statusCode":401}` |

### 2. POST /api/Auth/register  *(public)*
```json
// Request
{
  "email": "user@example.com",
  "name": "Jane Doe",
  "mobile": "+91 90000 00000",
  "password": "Passw0rd!",
  "profile_photo_url": null
}

// Response 201 (real, live) — same shape as login
{
  "success": true,
  "message": "Registration successful.",
  "data": {
    "token": "eyJhbG…",
    "expires_at": "2026-08-06T10:20:00Z",
    "user": { "id": "…", "email": "user@example.com", "name": "Jane Doe",
              "mobile": "+91 90000 00000", "profile_photo_url": null,
              "created_date": "2026-08-06T10:19:00Z", "updated_date": null, "role": "User" }
  },
  "status_code": 201
}

| Failure | Status | Body |
|---|---|---|
| Email taken | 409 | `{"success":false,"message":"A user with email 'user@example.com' already exists.","data":null,"statusCode":409}` |

> **New (local build only — not yet on deployed):** `POST /api/Auth/forgot-password` `{"email":"user@example.com"}` → `200 {"data":{"message":"…dev mode…","reset_token":"A1B2C3D4E5F6…","expires_minutes":15}}`  

> `POST /api/Auth/reset-password` `{"email":"user@example.com","reset_token":"A1B2…","new_password":"NewPassw0rd!"}` → `200 {"success":true,"message":"Password reset successful.","data":null,"statusCode":200}`

---
## 🎙️ Recordings

### 3. GET /api/Recordings?page=1&page_size=20  *(Bearer)*
```json
// Response 200 (real, live) — first item shown
{
  "success": true, "message": null,
  "data": {
    "page": 1, "page_size": 20, "total_count": 3,
    "items": [
      {
        "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
        "user_id": "22222222-2222-2222-2222-222222222222",
        "title": "Weekly Sprint Planning",
        "type": "meeting",
        "created_at": "2026-07-31T06:42:23.5939111",
        "duration": "00:42:00",
        "duration_seconds": 2520,
        "summary": "Sprint planning for Q3",
        "transcript": [{ "speaker": "A", "text": "Let's start…", "timestamp_seconds": 5 }],
        "actions": [{ "text": "Send follow-up email", "done": false }],
        "notes": [{ "id": "n1", "start_seconds": 0, "end_seconds": 12, "text": "Important point", "clip_path": null }],
        "is_recording": false,
        "bookmarked": true,
        "file_path": "uploads/recordings/…/….mp4",
        "source_language_code": "en-US",
        "transcription_status": "completed",
        "created_date": "2026-08-05T06:42:23.5937531",
        "updated_date": null
      }
    ]
  }
}

Query params: `page` (default 1), `page_size` (default 20, max 100), `search` (title/summary contains, case-insensitive), `userId` (admin only), `type`, `isBookmarked`, `sortBy`, `sortDesc`.

### 4. POST /api/Recordings  *(Bearer)*
```json
// Request
{
  "user_id": "string",
  "title": "string",
  "created_at": "2026-08-06T10:00:00Z",
  "duration": "00:42:10",
  "summary": "string",
  "transcript": [],
  "actions": [],
  "notes": [],
  "is_recording": false,
  "bookmarked": true,
  "file_path": "uploads/recordings/\u2026/\u2026.mp4",
  "source_language_code": "string",
  "duration_seconds": 2530
}

// Response 201 — the created recording (same shape as a list item)
```

### 5. GET /api/Recordings/{id}  *(Bearer)*
```json
// Response 200 (real, live)
{
  "success": true, "message": null,
  "data": {
    "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    "user_id": "22222222-2222-2222-2222-222222222222",
    "title": "verify sync",
    "type": "voice_note",
    "created_at": "2026-07-31T06:42:23.5939111",
    "duration": "00:00:02",
    "duration_seconds": 2,
    "summary": "s",
    "transcript": [{ "speaker": "A", "text": "sync test", "timestamp_seconds": 1 }],
    "actions": [{ "text": "a", "done": true }],
    "notes": [{ "id": "n1", "start_seconds": 0, "end_seconds": 1, "text": "n", "clip_path": null }],
    "is_recording": false,
    "bookmarked": true,
    "file_path": null,
    "source_language_code": "en-US",
    "transcription_status": "completed",
    "created_date": "2026-08-05T06:42:23.5937531",
    "updated_date": null
  }
}

| Failure | Status | Body |
|---|---|---|
| Not found / not yours | 404 | `{"success":false,"message":"Recording not found.","data":null,"statusCode":404}` |

### 6. PUT /api/Recordings/{id}  *(Bearer — full/partial update)*
```json
// Request (all fields optional; transcript/actions/notes round-trip as JSON)
{
  "title": "Renamed: Q3 Planning",
  "duration": "00:42:00",
  "summary": "Updated summary",
  "transcript": [],
  "actions": [],
  "notes": [],
  "is_recording": false,
  "bookmarked": true,
  "file_path": "uploads/recordings/\u2026/\u2026.mp4",
  "source_language_code": "en-US",
  "type": "meeting",
  "transcription_status": "completed"
}

// Response 200 — updated recording (real, live: title/bookmark/status/transcript all round-tripped)
```

### 7. DELETE /api/Recordings/{id}  *(Bearer)*
```json
// Response 200
{ "success": true, "message": "Recording deleted.", "data": null, "status_code": 200 }
```

### 8. PATCH /api/Recordings/{id}/bookmark  *(Bearer)*
```json
// Request
{
  "bookmarked": true
}

// Response 200 — `{ "success": true, "message": "Bookmark updated.", "data": true }`
```

### 9. GET /api/Recordings/batch?ids={id1}&ids={id2}  *(Bearer)*
```json
// Response 200 — list of recordings, order preserved, missing/not-owned skipped
```

### 10. POST /api/Recordings/batch  *(Bearer)*
```json
// Request
{
  "ids": [
    "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
  ]
}

// Response 200 (real, live): `{ "success": true, "data": [ …recording objects… ] }`  (max 100 ids)
```

### 11. POST /api/Recordings/play  *(Bearer — playlist)*
```json
// Request
{
  "ids": [
    "3fa85f64-5717-4562-b3fc-2c963f66afa6"
  ]
}

// Response 200
{
  "success": true,
  "data": [{
    "id": "3fa85f64-…", "title": "Weekly Sprint Planning", "type": "meeting",
    "duration": "00:42:00", "duration_seconds": 2520,
    "stream_url": "https://meetings-api.idealake.com/api/Recordings/3fa85f64-…/stream",
    "content_type": "audio/mp4"
  }]
}
```

### 12. GET /api/Recordings/{id}/stream  *(Bearer — HTTP Range supported)*
```json
// Response 206 Partial Content when a `Range: bytes=0-1023` header is sent
// Response 404 when the file is missing: {"success":false,"message":"Recording file not found.","data":null,"statusCode":404}
// Content-Type: video/mp4 | audio/mp4 | audio/wav | application/octet-stream (by file extension)
```

---
## ⬆️ Upload pipeline (resumable, chunked)

### 13. POST /api/Upload/start  *(Bearer)*
```json
// Request (real payload shape used by the app)
{
  "user_id": "22222222-2222-2222-2222-222222222222",
  "file_name": "team-sync.mp4",
  "total_chunks": 10,
  "source_language_code": "en-US",
  "file_size_bytes": 10485760,
  "duration": "00:42:00",
  "duration_seconds": 2520,
  "type": "meeting"
}

// Response 201 (real, live)
{
  "success": true,
  "message": "Upload batch started.",
  "data": {
    "batch_id": "2a941395-0696-413e-826c-ced4dfed85de",
    "total_chunks": 10,
    "upload_directory": "chunks/2a941395-0696-413e-826c-ced4dfed85de"
  },
  "status_code": 201
}
```

### 14. POST /api/Upload/chunk  *(Bearer — multipart/form-data)*
```
Form fields (camelCase! multipart binds to property names):
  batchId=<guid>        chunkNumber=1        totalChunks=10
  userId=<guid>         uploadedAt=2026-08-06T10:00:00Z
  summary, transcript, actions, notes  (JSON strings; metadata sent on the first chunk)
  file=@part1.bin  (the binary chunk; 60 MB max per chunk)

// Response 200 (real, live)
{
  "success": true, "message": null,
  "data": {
    "batch_id": "2a941395-…", "user_id": "22222222-…", "file_name": "team-sync.mp4",
    "total_chunks": 10, "received_chunks": [1], "is_complete": false,
    "status": "InProgress", "total_bytes_received": 1048576
  },
  "status_code": 200
}
```

### 15. GET /api/Upload/status/{batchId}  *(Bearer)*
```json
// Response 200 — same shape as the chunk response above
```

### 16. POST /api/Upload/retry  *(Bearer — re-send a failed chunk)*
```json
// Request
{
  "batch_id": "2a941395-0696-413e-826c-ced4dfed85de",
  "chunk_number": 3
}

// Response 200 — resets chunk 3 so it can be uploaded again
```

### 17. POST /api/Upload/complete/{batchId}  *(Bearer)*
```json
// Response 200 (real, live) — creates the Recording from the batch
{
  "success": true,
  "message": "Upload completed.",
  "data": {
    "id": "…", "title": "team-sync", "type": "meeting",
    "duration": "00:42:00", "duration_seconds": 2520,
    "transcript": [{ "speaker": "A", "text": "integration test", "timestamp_seconds": 1 }],
    "file_path": "uploads/recordings/2a9413950696413e826cced4dfed85de/2a941395-0696-413e-826c-ced4dfed85de.mp4",
    "transcription_status": "none", "bookmarked": false, "is_recording": false
  }
}
```

---
## 👤 Users (admin-scoped)

### 18. GET /api/Users?page=1&page_size=20  *(Bearer, admin)*
```json
// Response 200 — paged: { "data": { "page":1, "page_size":20, "total_count":2, "items":[ …UserResponse… ] } }
```

### 19. POST /api/Users  *(Bearer, admin)*
```json
// Request
{
  "email": "user@example.com",
  "name": "string",
  "mobile": "+91 90000 00000",
  "password": "Passw0rd!",
  "profile_photo_url": "https://example.com/photo.jpg"
}

// Response 201 — created UserResponse
```

### 20. GET /api/Users/{id}  *(Bearer, admin)*
```json
// Response 200 — UserResponse: { id, email, name, mobile, profile_photo_url, created_date, updated_date, role }
```

### 21. PUT /api/Users/{id}  *(Bearer, admin)*
```json
// Request
{
  "name": "string",
  "mobile": "+91 90000 00000",
  "profile_photo_url": "https://example.com/photo.jpg"
}

// Response 200 — updated UserResponse
```

### 22. DELETE /api/Users/{id}  *(Bearer, admin — soft delete)*
```json
// Response 200 — { "success": true, "message": "User deleted.", "data": null }
```

---
## Enums (string values)

| Field | Values |
|---|---|
| `type` | `audio`, `video`, `screen`, `meeting`, `other`, `interview`, `voice_note` |
| `transcription_status` | `none`, `processing`, `completed`, `failed` |
| Upload `status` | `InProgress` (int 1..4 internal) |
| `role` | `Admin`, `User` |