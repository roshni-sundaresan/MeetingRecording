# Meeting Recorder API

Production-ready ASP.NET Core 9 backend for a meeting recording application — user & recording management, JWT authentication, and a resumable **chunked upload pipeline** (start → chunk → status → retry → complete), built with Clean Architecture.

## Tech Stack

| Area | Choice |
|---|---|
| Runtime | ASP.NET Core 9 (C# 13, .NET 9) |
| Data | Entity Framework Core 9 (Code First) + SQL Server 2022 |
| Auth | JWT (HS256) with role claims |
| Mapping | AutoMapper 13 |
| Validation | FluentValidation 11 |
| Logging | Serilog (console + rolling file) |
| Rate limiting | Built-in `System.Threading.RateLimiting` (fixed window, per IP) |
| API docs | Swagger / OpenAPI (with JWT "Authorize" button) |
| Architecture | Clean Architecture, Repository + Unit of Work |
| Tests | xUnit, Moq, FluentAssertions |
| Containerization | Docker + docker-compose (API + SQL Server 2022) |

## Solution Layout

```
MeetingRecorder/
├── src/
│   ├── MeetingRecorder.Domain/          # Entities, enums, constants (no dependencies)
│   ├── MeetingRecorder.Application/     # DTOs, validators, services, interfaces, AutoMapper
│   ├── MeetingRecorder.Infrastructure/  # EF Core, repositories, UoW, JWT, chunk storage, seeding
│   └── MeetingRecorder.WebApi/          # Controllers, middleware, DI composition, Program.cs
├── tests/
│   └── MeetingRecorder.UnitTests/       # xUnit + Moq + FluentAssertions
├── postman/                             # Ready-to-import collection
├── Dockerfile                           # Multi-stage production build
└── docker-compose.yml                   # API + SQL Server 2022
```

**Dependency rule:** Domain ← Application ← Infrastructure ← WebApi. Every layer depends only inward; the WebApi is the only composition root.

## Prerequisites

- .NET 9 SDK (`dotnet --version` → `9.x`)
- SQL Server 2022 — or Docker Desktop (recommended on macOS: `brew install colima docker` and start colima)
- Postman (optional) — import `postman/MeetingRecorder.postman_collection.json`
- EF Core CLI: `dotnet tool install --global dotnet-ef`

> Windows-only tooling (Visual Studio 2022, SSMS, IIS Express) is optional — VS Code + the `dotnet` CLI cover everything here. IIS Express is not used; Kestrel hosts the app.

## Quick Start (Docker — easiest)

```bash
docker compose up --build          # SQL Server on :1433, API on :8080
# API:  http://localhost:8080/swagger
```

The API runs EF migrations and seeds data on startup (see `Database:MigrateOnStartup` / `Database:SeedOnStartup`).

> **Apple Silicon note:** the SQL Server 2022/2025 images are amd64-only and crash under QEMU
> emulation (`Invalid mapping of address ...`). On an M-series Mac use the ARM64-native
> Azure SQL Edge image for local dev instead:
>
> ```bash
> docker run -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=YourStrong!Passw0rd" \
>   -p 1433:1433 -d mcr.microsoft.com/azure-sql-edge:latest
> ```

## Local Development

```bash
# 1. Start SQL Server (Docker Desktop / colima)
docker run -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=YourStrong!Passw0rd" \
  -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest

# 2. Restore + build
dotnet restore MeetingRecorder.sln
dotnet build MeetingRecorder.sln

# 3. Create the database (Code First migration)
dotnet ef database update --project src/MeetingRecorder.Infrastructure \
                          --startup-project src/MeetingRecorder.WebApi

# 4. Run
dotnet run --project src/MeetingRecorder.WebApi --launch-profile http
# Swagger: http://localhost:5080/swagger
```

To regenerate migrations after model changes:

```bash
dotnet ef migrations add <Name> --project src/MeetingRecorder.Infrastructure \
                                --startup-project src/MeetingRecorder.WebApi
```

## Seed Accounts

| Role | Email | Password |
|---|---|---|
| Admin | `admin@meetingrecorder.dev` | `Admin@123` |
| User | `demo@meetingrecorder.dev` | `Demo@123` |

Seed data also includes three sample recordings (one bookmarked) so list/bookmark endpoints show results immediately.

## API Overview

All endpoints return the standard envelope:

```json
{ "success": true, "message": "...", "data": { }, "errors": null, "statusCode": 200 }
```

### Auth (public)
| Method | Route | Description |
|---|---|---|
| POST | `/api/auth/login` | Exchange email+password for a JWT |
| POST | `/api/auth/register` | Create account, returns JWT |

### Users (JWT required; list/create are admin-only, others self-or-admin)
| Method | Route | Description |
|---|---|---|
| GET | `/api/users?page=&pageSize=&search=&sortBy=&sortOrder=` | Paged, searchable, sortable list |
| GET | `/api/users/{id}` | Single user |
| POST | `/api/users` | Create (admin) |
| PUT | `/api/users/{id}` | Update |
| DELETE | `/api/users/{id}` | Soft delete |

### Recordings (JWT required; owner-or-admin)
| Method | Route | Description |
|---|---|---|
| GET | `/api/recordings?userId=&page=&pageSize=&search=&sortBy=&sortOrder=` | Paged list (users see only their own) |
| GET | `/api/recordings/{id}` | Single recording |
| POST | `/api/recordings` | Create |
| PUT | `/api/recordings/{id}` | Update |
| PATCH | `/api/recordings/{id}/bookmark` | `{ "bookmarked": true }` |
| DELETE | `/api/recordings/{id}` | Soft delete |

### Batch Upload (JWT required; stricter rate limit 60/min)
| Method | Route | Description |
|---|---|---|
| POST | `/api/upload/start` | `{ userId, fileName, type, totalChunks, sourceLanguageCode, ... }` → `batchId` |
| POST | `/api/upload/chunk` | **multipart/form-data**: file + `batchId, chunkNumber, totalChunks, userId, uploadedAt, summary, transcript, actions, notes` |
| GET | `/api/upload/status/{batchId}` | Received chunks, `isComplete`, bytes received |
| POST | `/api/upload/retry` | `{ batchId, chunkNumber }` — resets a chunk for re-upload |
| POST | `/api/upload/complete/{batchId}` | Validate → merge in order → save recording → delete temp chunks → mark completed |

**Chunked upload flow** (matches the spec):

1. **Start Upload** — create the batch, get `batchId`.
2. **Upload Chunk** — send each part (1..N). Idempotent: re-sending a chunk overwrites it, so any chunk can be retried at any time.
3. **Upload Status** — poll to see which chunks arrived.
4. **Retry Chunk** — reset a missing/corrupt chunk, then re-send via `/chunk`.
5. **Complete Upload** — when all chunks (validated for count + sequence) are present: merges binaries **in order**, saves the final `Recording` (chunk metadata — summary/transcript/actions/notes — rides along to the recording), **deletes temporary chunks** (files + DB rows), and marks the batch `Completed`. A per-batch lock prevents double-completion races.

Chunks are stored under `uploads/chunks/{batchId}/` and merged files under `uploads/recordings/{batchId}/` (configurable via `Storage:RootPath`). All paths derive from GUIDs + integers — no user input touches the filesystem path, so path traversal is impossible.

## Security Features

- **JWT** — HS256, issuer/audience/lifetime validated, roles in claims; passwords hashed with **BCrypt** (cost 12).
- **SQL injection** — EF Core parameterizes all queries; sorting uses a **whitelist** of allowed columns (no user input reaches expressions); no raw SQL.
- **XSS** — input length limits + regex validation on all free-text fields; API returns JSON only (client-side rendering must escape content).
- **Rate limiting** — 300 req/min per IP globally, 60 req/min on upload endpoints, HTTP 429 + JSON envelope on rejection.
- **HTTPS** — `UseHttpsRedirection` (dev certs: `dotnet dev-certs https --trust`); disabled inside the container where the load balancer terminates TLS.
- **CORS** — allow-list from `Cors:AllowedOrigins`; only configured origins can call the API.
- **Global exception middleware** — every error becomes the standard envelope; internal details are never leaked (500s log full stack to Serilog).
- **Ownership checks** — regular users can only read/write their own data; admin role bypasses.
- **Soft deletes** — `IsDeleted` + global query filters keep the audit trail.

## Observability

Serilog writes to console and `logs/log-YYYYMMDD.txt` (14-day retention). Every request failure is logged with method/path/status; unhandled exceptions log full stack traces. Add any Serilog sink (Seq, Application Insights, OpenTelemetry…) via `Serilog:WriteTo` in `appsettings.json`.

## Testing

```bash
dotnet test MeetingRecorder.sln            # 33 unit tests (xUnit + Moq + FluentAssertions)
python3 smoke_test.py                      # end-to-end smoke test against a running API
```

Unit tests cover: auth flows (login/register/duplicate/password mismatch), user & recording CRUD (soft delete, partial updates, access failures), the full batch-upload pipeline (chunk validation, missing-chunk detection, merge + cleanup + completion), JWT round-trips (valid / tampered / wrong key), and all FluentValidation rules. The smoke test drives a live instance: Swagger, auth, CRUD, an out-of-order 3-chunk upload, merge + cleanup verification, the retry flow, and error envelopes.

## Postman

Import `postman/MeetingRecorder.postman_collection.json`. Run **Auth → Login** first — the collection variable `token` is set automatically and injected into every other request as `Authorization: Bearer {{token}}`. For the upload flow, create three small files (`/tmp/chunk_1.bin` etc.) and run the Batch Upload folder top-to-bottom.

## Configuration Reference (`appsettings.json`)

| Section | Key | Default |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | SQL Server connection string | `localhost,1433` sa / `YourStrong!Passw0rd` |
| `Jwt:Key` | Signing key (≥32 chars; **change in production**) | dev key |
| `Jwt:ExpiryMinutes` | Token lifetime | 120 |
| `Storage:RootPath` | Chunk + final file root | `uploads` |
| `Cors:AllowedOrigins` | Allowed origins array | localhost:3000/5173/8080 |
| `Https:EnableRedirection` | HTTPS redirect on/off | true (false in container) |
| `Database:MigrateOnStartup` / `SeedOnStartup` | Auto-migrate + seed | true |

## Production Checklist

- [ ] Replace `Jwt:Key` with a strong secret (Key Vault / env var)
- [ ] Replace the SA password / use Azure SQL managed identity
- [ ] Put uploads on blob storage (Azure Blob / S3) by swapping `IChunkStorageService`
- [ ] TLS termination at the gateway (keep `Https:EnableRedirection=false` in the container)
- [ ] Add a load-balancer rate-limit strategy if scaling horizontally (the built-in limiter is per-instance)
- [ ] Run `dotnet ef migrations script` and review the SQL in CI
