# Deployment & Operations Guide

How to run the Meeting Recorder API in production, with special focus on
**password-reset OTP email delivery** (the most commonly misconfigured piece).

## 1. SMTP configuration (REQUIRED for password-reset emails)

The password-reset flow generates a 6-digit OTP server-side and emails it to
the user. If SMTP is not configured the API **silently skips the email**
(by design, to avoid account enumeration) and returns the generic success
message — the caller sees `"reset_request_id": null` only when the account
doesn't exist, but the email never goes out either way.

Configure SMTP **without touching the repo** using environment variables
(ASP.NET Core reads `Email__Smtp__*` over `appsettings.json`):

```bash
export Email__Smtp__Host="<smtp-host>"          # e.g. smtp-relay.corp.local
export Email__Smtp__Port="587"                   # 25 / 465 / 587
export Email__Smtp__Username="<user>"            # empty = anonymous
export Email__Smtp__Password="<password>"
export Email__Smtp__From="no-reply@yourdomain.com"
export Email__Smtp__UseSsl="true"                # false for port 25 / internal relays
```

Also verify (production defaults are already safe):

```bash
export PasswordReset__DevOtpExposure="false"     # MUST stay false in prod
```

### How to verify it's live

1. Start the API. On boot it now logs a warning if SMTP is missing:
   `SMTP is NOT configured ... OTP emails will be SKIPPED`.
2. Ensure the account exists **before** testing:
   ```bash
   curl -sk https://meetings-api.example.com/api/Auth/password-reset/request \
     -H 'Content-Type: application/json' \
     -d '{"username":"existing-user@example.com"}'
   ```
   - `reset_request_id` **non-null** → account exists; OTP created + emailed.
   - `reset_request_id` **null** → account not registered in this DB. Register
     it first via `POST /api/Auth/register` (or seed it), then retry.
3. The recipient should get "Password Reset OTP" within seconds.

### SMTP failure modes

| Symptom | Cause | Fix |
|---|---|---|
| Email never arrives, request returns 200 generic | `Email__Smtp__Host` empty | Set the env vars above, restart |
| Send fails, request still 200 | Wrong host/port/creds, TLS mismatch, sender not allowed | Check the API log line `Failed to send password-reset OTP email` (OTP is never logged); correct SMTP settings |
| `dev_otp` appears in response | `PasswordReset__DevOtpExposure=true` in prod | Set to `false` |

Emails are sent synchronously; a slow/unreachable relay adds latency to the
request but failures never leak account existence to the caller.

## 2. Database

- The API runs EF migrations on startup (`Database:MigrateOnStartup=true`).
- SQL Server connection: `ConnectionStrings__DefaultConnection` env var.
- On Apple Silicon dev machines use Azure SQL Edge (arm64) — the SQL Server
  2022/2025 amd64 images crash under QEMU.

## 3. Publish & run (Linux/systemd example)

```bash
dotnet publish src/MeetingRecorder.WebApi -c Release -o /opt/meetingrecorder
# set env vars (see §1), then start:
dotnet /opt/meetingrecorder/MeetingRecorder.WebApi.dll
```

systemd unit snippet:

```ini
[Service]
Environment=Email__Smtp__Host=...
Environment=Email__Smtp__Port=587
Environment=Email__Smtp__Username=...
Environment=Email__Smtp__Password=...
Environment=Email__Smtp__From=no-reply@yourdomain.com
ExecStart=/usr/bin/dotnet /opt/meetingrecorder/MeetingRecorder.WebApi.dll
```

## 4. Verification checklist after every deploy

- [ ] `GET /swagger` reachable
- [ ] Login with a seeded account → JWT + refresh token
- [ ] `POST /api/Auth/password-reset/request` for a **registered** account →
      non-null `reset_request_id` AND the email arrives
- [ ] `POST /api/Auth/password-reset/verify-otp` with the emailed OTP →
      `reset_token` returned
- [ ] `POST /api/Auth/password-reset/complete` with the token → password
      changed, all sessions revoked, old password no longer logs in
