# Security Posture

This document describes the security controls that are implemented in the current `AtmMachine.WebUI` host. It is intentionally specific to the code in this repository.

Relevant code:
- `src/AtmMachine.WebUI/Banking/Services/BankingSecurityService.cs`
- `src/AtmMachine.WebUI/Banking/Services/BankingService.cs`
- `src/AtmMachine.WebUI/Program.cs`
- `src/AtmMachine.WebUI/Infrastructure/AuthCookieExtensions.cs`
- `src/AtmMachine.WebUI/Infrastructure/SecurityHeadersMiddleware.cs`

## Auth Model

- Interactive sign-in is handled by `BankingService.LoginAsync(...)`.
- Successful login creates two auth layers:
  - an ASP.NET Core session cookie named `consoleatm.session`
  - an `access_token` cookie plus a `refresh_token` cookie
- Razor Pages use session state for page access checks.
- Minimal API endpoints accept either:
  - the authenticated session
  - a valid `access_token` cookie via `TryAuthenticate(...)`
- Access tokens are custom HMAC-SHA256 signed bearer tokens issued by `BankingSecurityService`.
- Access token lifetime is 20 minutes.
- Refresh token lifetime is 30 days.
- Refresh tokens are stored per device in `BankUser.RefreshSessions` and are rotated on refresh.
- Device records and login history are recorded on successful sign-in, including IP address and user agent.
- New-device logins generate an in-app security alert when `SecurityAlertsEnabled` is enabled.

## Password Hashing

This project does not currently use ASP.NET Core Identity. Password storage is implemented directly in `BankingSecurityService`.

- Algorithm: PBKDF2 (`Rfc2898DeriveBytes.Pbkdf2`)
- Hash function: SHA-256
- Iterations: 120,000
- Salt: 16 random bytes per password
- Derived key size: 32 bytes
- Verification uses `CryptographicOperations.FixedTimeEquals` to avoid timing leaks during hash comparison
- Minimum password length: 8 characters

## Lockout Policy

Lockout is enforced in `BankingService.LoginAsync(...)`.

- Failed password attempts are counted per user
- After 5 consecutive failed password attempts, the account is locked
- Lockout duration is 15 minutes
- On lockout:
  - `FailedLoginAttempts` is reset
  - `LockedUntilUtc` is set
  - an audit entry is written with action `login_lockout`
- On successful login:
  - `FailedLoginAttempts` is reset to `0`
  - `LockedUntilUtc` is cleared

## Rate Limiting Policy

Rate limiting is configured in `src/AtmMachine.WebUI/Program.cs`.

- `auth` policy:
  - algorithm: fixed window
  - limit: 6 requests
  - window: 1 minute
  - queue: 0
  - applied to `src/AtmMachine.WebUI/Pages/Login.cshtml.cs`
- `transfers` policy:
  - algorithm: token bucket
  - token limit: 10
  - refill: 10 tokens every 1 minute
  - queue: 0
  - applied to:
    - `src/AtmMachine.WebUI/Pages/Transfers.cshtml.cs`
    - `POST /api/v1/transfers/internal`
    - `POST /api/v1/transfers/external`
    - `POST /api/v1/money/income`
    - `POST /api/v1/money/expense`

## Threat Model

Primary threats this project actively defends against:

- Online password guessing and basic credential stuffing:
  - rate limiting on login
  - account lockout after repeated failures
- Token tampering:
  - access tokens are signed with HMAC-SHA256 and rejected when signatures or expiry are invalid
- Duplicate transfer submission:
  - transfer creation uses idempotency keys and rejects duplicate money movement requests
- Clickjacking:
  - `X-Frame-Options: DENY`
  - `frame-ancestors 'none'` in CSP
- Basic script injection blast radius:
  - restrictive `Content-Security-Policy` that only allows same-origin scripts
- Privileged action repudiation:
  - money actions, freezes, dispute actions, and logins are written to the audit log

Current boundaries and known gaps:

- This is a banking simulator, not a regulated banking platform
- MFA is not currently enforced in the shipped login path
- Session state uses in-memory cache, so it is not suitable for multi-node production deployments without moving to a distributed backing store
- The app does not currently integrate with an external key vault or hardware-backed secret store

## Secrets Management

Secrets should be injected through configuration providers, not committed into source control.

Supported practice in this repository:

- Environment variables
- `dotnet user-secrets` for local development on `src/AtmMachine.WebUI/AtmMachine.WebUI.csproj`

Never commit real values for:

- `Banking:Security:JwtSigningKey`
- `Banking:Firebase:ApiKey`
- `Banking:Firebase:ServicePassword`
- `Banking:Appwrite:ApiKey`

Recommended local setup:

```bash
dotnet user-secrets set "Banking:Security:JwtSigningKey" "replace-with-at-least-32-bytes" --project src/AtmMachine.WebUI/AtmMachine.WebUI.csproj
dotnet user-secrets set "Banking:Firebase:ApiKey" "replace-me" --project src/AtmMachine.WebUI/AtmMachine.WebUI.csproj
```

Implementation detail:

- If `Banking:Security:JwtSigningKey` is not configured, the app falls back to an in-memory random signing key.
- That fallback is acceptable for local development, but it invalidates issued access tokens on restart and should not be relied on for persistent environments.

## Security Headers

Security headers are applied by `src/AtmMachine.WebUI/Infrastructure/SecurityHeadersMiddleware.cs`.

- `Content-Security-Policy`
- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: DENY`
- `Referrer-Policy: no-referrer`
- `Permissions-Policy`

Transport control:

- `UseHsts()` is enabled outside `Development`
- `UseHttpsRedirection()` is enabled in all environments

Cookie hardening:

- Session and auth cookies are `HttpOnly`
- Session and auth cookies use `SameSite=Lax`
- Auth cookies are marked `Secure` on HTTPS requests
- Production session cookies are forced to `Secure` via `CookieSecurePolicy.Always`

## RBAC

Roles are represented by `UserRole`.

`User` capabilities:

- Access only their own profile, accounts, devices, login history, statements, transactions, transfers, budgets, goals, and disputes
- Create internal and external simulated transfers from their own accounts
- Link their own external accounts
- Freeze or unfreeze their own accounts from settings
- Create dispute tickets tied to their own transactions

`Admin` capabilities:

- View all users and all non-system accounts in the admin console
- Freeze or unfreeze any non-system user account
- Post manual balance adjustments
- Review and change dispute status
- View the audit log feed exposed in the admin UI and `GET /api/v1/admin/audit-logs`

Authorization enforcement:

- UI admin actions check `UserRole.Admin` in `src/AtmMachine.WebUI/Pages/Admin.cshtml.cs`
- Admin API reads also require `UserRole.Admin` in `src/AtmMachine.WebUI/Program.cs`
- Service-layer admin operations verify the acting user is still an admin before mutating data

## Auditability

Security-sensitive and money-sensitive actions write audit records in `database.AuditLogs`.

Examples:

- `login_success`
- `login_lockout`
- `create_internal_transfer`
- `create_external_transfer`
- `freeze_account`
- `manual_adjustment`
- `create_dispute`
- `review_dispute`
