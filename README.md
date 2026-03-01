<div align="center">
  <h2 align="center">ConsoleATM Digital Banking</h2>
  <div align="left">

![Repo Views](https://visitor-badge.laobi.icu/badge?page_id=SpencerVJones/ConsoleATM)
</div>

  <p align="center">
    A portfolio-grade .NET banking simulation with an append-only double-entry ledger, fraud scoring, event-driven settlement, and real-time operational alerts.
    <br />
    <br />
    <a href="https://github.com/SpencerVJones/ConsoleATM/issues">Report Bug</a>
    ·
    <a href="https://github.com/SpencerVJones/ConsoleATM/issues">Request Feature</a>
  </p>
</div>


<!-- PROJECT SHIELDS -->
<div align="center">

![License](https://img.shields.io/github/license/SpencerVJones/ConsoleATM?style=for-the-badge)
![Contributors](https://img.shields.io/github/contributors/SpencerVJones/ConsoleATM?style=for-the-badge)
![Forks](https://img.shields.io/github/forks/SpencerVJones/ConsoleATM?style=for-the-badge)
![Stargazers](https://img.shields.io/github/stars/SpencerVJones/ConsoleATM?style=for-the-badge)
![Issues](https://img.shields.io/github/issues/SpencerVJones/ConsoleATM?style=for-the-badge)
![Last Commit](https://img.shields.io/github/last-commit/SpencerVJones/ConsoleATM?style=for-the-badge)
![Repo Size](https://img.shields.io/github/repo-size/SpencerVJones/ConsoleATM?style=for-the-badge)
![Platform](https://img.shields.io/badge/platform-Web%20%7C%20Console-lightgrey.svg?style=for-the-badge)
![API](https://img.shields.io/badge/API-REST%20v1-0ea5e9.svg?style=for-the-badge)
![.NET](https://img.shields.io/badge/.NET-9-512BD4.svg?style=for-the-badge)
![Razor Pages](https://img.shields.io/badge/Razor%20Pages-UI-0ea5e9.svg?style=for-the-badge)
![Storage](https://img.shields.io/badge/Storage-PostgreSQL%20%7C%20Firebase%20%7C%20SQLite%20%7C%20JSON%20%7C%20Appwrite-16a34a.svg?style=for-the-badge)
![xUnit](https://img.shields.io/badge/xUnit-Testing-25A162.svg?style=for-the-badge)
![Security](https://img.shields.io/badge/Security-Password%20Auth%20%7C%20Lockout%20%7C%20Audit-1d4ed8.svg?style=for-the-badge)
</div>


## 📑 Table of Contents
- [Overview](#overview)
- [Technologies Used](#technologies-used)
- [Features](#features)
- [Demo](#demo)
- [Project Structure](#project-structure)
- [Money Movement](#money-movement)
- [Risk & Eventing](#risk--eventing)
- [API Endpoints](#api-endpoints)
- [API Examples](#api-examples)
- [Deploy to Render](#deploy-to-render)
- [Testing](#testing)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [Installation](#installation)
  - [Run Web App](#run-web-app)
  - [Run Console App](#run-console-app)
- [Usage](#usage)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
  - [Contributors](#contributors)
- [License](#license)
- [Contact](#contact)

## Overview
**ConsoleATM** includes:
- A modern ASP.NET Core Razor Pages banking UI
- Layered domain/services/data architecture
- Secure sign-up/sign-in with lockout protection and audit logging
- Transfer simulation with pending/posted lifecycle and settlement worker
- Admin console for account controls, disputes, and audit logs

Users can:
- Create accounts and sign in securely
- View balances, transactions, budgets, and savings goals
- Submit disputes and manage profile/security settings
- Transfer funds internally or simulate ACH transfers externally

Admins can:
- Review users/accounts/balances
- Freeze or unfreeze accounts
- Post manual adjustments
- Review disputes and audit actions

## Technologies Used
- C#
- ASP.NET Core Razor Pages + Minimal APIs
- .NET 9 SDK
- Firebase-backed auth support with PostgreSQL/SQLite/JSON/Appwrite persistence provider options
- `System.Text.Json`
- Hosted background service for settlement processing
- xUnit (test project)

## Features
- 🔐 Signup/login with password auth + lockout controls
- 👤 Profile management (name, address, phone, settings)
- 🏦 Checking/savings accounts with masked account numbers
- 🪪 Open additional checking/savings accounts ("add card") with optional opening deposit
- 📊 Spending analytics and month-vs-last-month metrics
- 💸 Internal + external (mock ACH) transfers
- 🤝 Peer-to-peer transfers to other users (from your own account)
- ⏱ Scheduled and recurring transfer support
- 💰 Quick cash-in/cash-out actions so users can gain/lose money intentionally
- 🧾 Statement export (CSV + pseudo-PDF)
- 🧰 Budgets and savings goals
- 🚨 Notifications, login history, device tracking, freeze controls
- 🧷 Dispute ticket workflow (user + admin review)
- 🛡 Audit log for security-sensitive actions
- ⚙️ API versioning at `/api/v1` + transfer idempotency + rate limits
- 📚 Append-only double-entry ledger with balances derived from ledger entries
- 🚩 Fraud scoring rules with manual transfer review (`UnderReview`) and admin approval/rejection
- 📡 SignalR-powered live transfer status, balance updates, fraud alerts, and admin audit feed
- 📨 In-memory domain event stream for transfer/dispute lifecycle events
- 📈 Observability: structured JSON logs, correlation IDs, trace IDs, and `/metrics`

## Security
See `SECURITY.md` for the implemented auth model, lockout thresholds, rate limits, secrets handling, security headers, and RBAC boundaries.

## Demo
Local run URL:
- `http://127.0.0.1:5074/Login`

Demo credentials:
- User: `spencer@example.com` / `Password123!`
- Admin: `admin@consoleatm.local` / `Admin123!`

## Project Structure
```bash
ConsoleATM/
├── README.md
├── LICENSE
├── ConsoleATM.sln
├── data/
│   ├── accounts.json
│   └── banking.json
├── src/
│   ├── AtmMachine.Domain/
│   │   ├── Abstractions/
│   │   └── Models/
│   ├── AtmMachine.Services/
│   │   ├── Abstractions/
│   │   └── Models/
│   ├── AtmMachine.Data/
│   ├── AtmMachine.ConsoleUI/
│   └── AtmMachine.WebUI/
│       ├── Banking/
│       │   ├── Infrastructure/
│       │   ├── Models/
│       │   └── Services/
│       ├── Infrastructure/
│       ├── Pages/
│       └── wwwroot/
├── tests/
│   └── AtmMachine.Tests/
├── Dockerfile
├── .dockerignore
└── render.yaml
```

## Money Movement
Balances are derived from append-only ledger entries, not from directly mutating account totals.

Transfer flow:
```text
Transfer Requested
      |
      v
   PENDING
      |
      v
Settlement Worker
      |
      v
   POSTED
```

Ledger write pattern (append-only):
```text
1. Validate idempotencyKey
2. Create correlationId
3. Append DEBIT ledger entry
4. Append CREDIT ledger entry
5. Recompute derived account balances from the ledger
6. Emit user-facing transaction history
```

Each money movement record carries:
- `correlationId`
- `idempotencyKey`
- `createdBy` (`user`, `admin`, `system`)
- immutable ledger rows (new ledger rows are appended instead of updating prior money records)

Fraud review path:
```text
Transfer Requested
      |
      v
Fraud Rules (velocity / amount / device / IP)
      |
      +--> UNDER REVIEW --> Admin Approve/Reject
      |
      +--> PENDING/POSTED
```

## Risk & Eventing
Fraud controls, domain events, and realtime delivery are wired together so high-risk transfers behave like a real fintech workflow.

Fraud rules currently score transfers using:
- velocity spikes (`X` transfers in `Y` minutes)
- transfer amount thresholds
- balance-drain behavior
- new device logins
- IP changes across recent logins
- newly linked external accounts

When a transfer crosses the fraud threshold:
- the transfer moves to `UnderReview`
- the user receives a transfer status notification
- admins receive a live fraud alert
- the transfer appears in the admin fraud queue

Domain event stream:
```text
TransferCreated
   -> Fraud analyzer
   -> Notification dispatch
   -> Audit logging

TransferSettled
   -> Balance refresh
   -> User notification

DisputeOpened
   -> Admin review queue
   -> Audit trail
```

Realtime delivery:
```text
Service action
   -> enqueue realtime envelope
   -> background dispatch worker
   -> SignalR hub group (user/admin)
   -> browser toast / live audit feed / balance refresh
```

## API Endpoints
Base route: `/api/v1`

- `GET /api/v1/health` - service status
- `GET /api/v1/accounts` - list current user accounts
- `POST /api/v1/accounts` - open a new checking/savings account
- `GET /api/v1/transactions` - query ledger with filters
- `POST /api/v1/transfers/internal` - create internal transfer
- `GET /api/v1/transfers/recipients` - list other-user recipient accounts
- `POST /api/v1/transfers/external` - create external transfer
- `GET /api/v1/notifications` - current user notifications
- `GET /api/v1/admin/audit-logs` - admin audit feed
- `GET /metrics` - Prometheus-style counters for requests, transfers, disputes, and worker runs

Notes:
- Authenticated endpoints require a valid app session or `access_token` cookie.
- Transfer endpoints support `Idempotency-Key` header.
- Responses include `X-Correlation-ID` and `X-Trace-ID` headers for request tracing.
- Money simulation endpoints:
  - `POST /api/v1/money/income`
  - `POST /api/v1/money/expense`

## API Examples
Health check:
```bash
curl -s http://127.0.0.1:5074/api/v1/health
```

Ledger query (authenticated request example):
```bash
curl -s "http://127.0.0.1:5074/api/v1/transactions?state=Posted&category=Dining"
```

Internal transfer payload:
```json
{
  "sourceAccountId": "00000000-0000-0000-0000-000000000001",
  "destinationAccountId": "00000000-0000-0000-0000-000000000002",
  "amount": 25.75,
  "memo": "Dinner split",
  "scheduledForUtc": null,
  "frequency": "OneTime"
}
```

Transfer request example:
```bash
curl -X POST http://127.0.0.1:5074/api/v1/transfers/internal \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: transfer-demo-001" \
  -d '{
    "sourceAccountId":"00000000-0000-0000-0000-000000000001",
    "destinationAccountId":"00000000-0000-0000-0000-000000000002",
    "amount":25.75,
    "memo":"Dinner split",
    "scheduledForUtc":null,
    "frequency":"OneTime"
  }'
```

Open account request example:
```bash
curl -X POST http://127.0.0.1:5074/api/v1/accounts \
  -H "Content-Type: application/json" \
  -d '{
    "accountType":"Checking",
    "nickname":"Travel Card",
    "openingDeposit":50.00,
    "fundingSourceAccountId":"00000000-0000-0000-0000-000000000001"
  }'
```

## Deploy to Render
Render deployment is configured in this repo:
- `Dockerfile` for production container build
- `render.yaml` Blueprint for one-click Render setup
- runtime entrypoint that binds Kestrel to Render `PORT`

### Option A: Blueprint (recommended)
1. Push this repo to GitHub.
2. In Render, click `New +` -> `Blueprint`.
3. Select this repository.
4. In the generated web service, set `NEON_DATABASE_URL` to your Neon connection string.
5. Deploy.

### Option B: Manual Web Service (if you skip Blueprint)
1. In Render, click `New +` -> `Web Service`.
2. Connect this repository.
3. Configure:
   - Runtime: `Docker`
   - Dockerfile path: `./Dockerfile`
4. Add environment variables listed below.
5. Deploy.

Important env vars for Render:
- `Banking__Provider=neon`
- `NEON_DATABASE_URL=postgresql://...`
- `Banking__Security__JwtSigningKey` (auto-generated by `render.yaml`)
- `Banking__Firebase__Enabled=false`
- `Banking__Postgres__StateKey=bank_state` (optional override)

After deploy, verify:
```bash
curl -s https://<your-render-service>.onrender.com/api/v1/health
```
Open:
```text
https://<your-render-service>.onrender.com/Login
```

Local Docker smoke test (optional):
```bash
docker build -t consoleatm .
docker run --rm -p 10000:10000 \
  -e PORT=10000 \
  -e Banking__Provider=neon \
  -e NEON_DATABASE_URL="<YOUR_NEON_URL>" \
  -e Banking__Security__JwtSigningKey="<LONG_RANDOM_SECRET_32+_BYTES>" \
  consoleatm
```
Open:
```text
http://127.0.0.1:10000/Login
```

GitHub Pages note:
- This project cannot run fully on GitHub Pages because it requires a live ASP.NET Core backend (Razor Pages, APIs, sessions, workers, and SignalR).
- You can use GitHub Pages for a static landing/docs site, and host the app backend on a runtime platform.

GitHub metadata:
- See `gitinfo` for a ready-to-paste repository description and topic tags.

## Testing
Automated test project is included:
- `tests/AtmMachine.Tests`
- Includes integration-style flow tests for:
  - login
  - open account with initial funding (double-entry)
  - transfer settlement (pending -> posted)
  - dispute lifecycle (user create -> admin resolve)

Run tests:
```bash
dotnet test tests/AtmMachine.Tests/AtmMachine.Tests.csproj
```

## Getting Started
### Prerequisites
- .NET SDK 9.0+
- macOS, Linux, or Windows

### Installation
1. Clone the repository:
```bash
git clone https://github.com/SpencerVJones/ConsoleATM.git
```
2. Move into the project:
```bash
cd ConsoleATM
```
3. Build:
```bash
dotnet build src/AtmMachine.WebUI/AtmMachine.WebUI.csproj
```

### Run Web App
```bash
dotnet run --project src/AtmMachine.WebUI/AtmMachine.WebUI.csproj --launch-profile http
```
Open:
```text
http://127.0.0.1:5074/Login
```

Persistence provider defaults to SQLite in this repo configuration.
Optional override:
```bash
Banking__Provider=json dotnet run --project src/AtmMachine.WebUI/AtmMachine.WebUI.csproj --launch-profile http
```

Use Firebase (auth + Firestore-backed state):
```bash
Banking__Provider=firebase \
Banking__Firebase__Enabled=true \
Banking__Firebase__ApiKey=<FIREBASE_API_KEY> \
Banking__Firebase__ProjectId=<FIREBASE_PROJECT_ID> \
Banking__Firebase__CollectionId=banking_state \
Banking__Firebase__DocumentId=bank_state \
Banking__Firebase__ServiceEmail=<FIREBASE_SERVICE_USER_EMAIL> \
Banking__Firebase__ServicePassword=<FIREBASE_SERVICE_USER_PASSWORD> \
dotnet run --project src/AtmMachine.WebUI/AtmMachine.WebUI.csproj --launch-profile http
```

Firebase Firestore document requirements:
- Collection id (default in appsettings): `banking_state`
- Document id (default): `bank_state`
- Fields:
  - `payload` string
  - `updatedUtc` string
- If your Firestore rules require authenticated access, set `ServiceEmail` + `ServicePassword` so the backend writes with a Firebase ID token.

Use Appwrite as the database backend:
```bash
Banking__Provider=appwrite \
Banking__Appwrite__Endpoint=https://cloud.appwrite.io/v1 \
Banking__Appwrite__ProjectId=<PROJECT_ID> \
Banking__Appwrite__ApiKey=<API_KEY> \
Banking__Appwrite__DatabaseId=<DATABASE_ID> \
Banking__Appwrite__CollectionId=<COLLECTION_ID> \
Banking__Appwrite__DocumentId=bank_state \
dotnet run --project src/AtmMachine.WebUI/AtmMachine.WebUI.csproj --launch-profile http
```

Appwrite collection requirements:
- `payload` string attribute
- `updatedUtc` string attribute
- a document id (default: `bank_state`) accessible by your API key

Use PostgreSQL for transactional state persistence:
```bash
Banking__Provider=postgres \
Banking__Postgres__ConnectionString="Host=localhost;Port=5432;Database=consoleatm;Username=postgres;Password=<PASSWORD>;Pooling=true" \
Banking__Postgres__StateKey=bank_state \
dotnet run --project src/AtmMachine.WebUI/AtmMachine.WebUI.csproj --launch-profile http
```

Use Neon (recommended hosted Postgres):
```bash
Banking__Provider=neon \
NEON_DATABASE_URL="postgresql://<USER>:<PASSWORD>@<HOST>/<DATABASE>?sslmode=require" \
dotnet run --project src/AtmMachine.WebUI/AtmMachine.WebUI.csproj --launch-profile http
```

Neon notes:
- `Banking__Provider=neon` is an alias for the PostgreSQL provider
- the app accepts either standard Npgsql connection strings or Neon-style `postgresql://...` URLs
- it will also read `DATABASE_URL` if you prefer that convention
- if SSL options are missing, the app defaults to `SSL Mode=Require` and `Trust Server Certificate=true`

PostgreSQL store behavior:
- Creates relational tables automatically on first run:
  - `bank_runtime_state`
  - `bank_users`
  - `bank_accounts`
  - `bank_ledger_entries`
  - `bank_transactions`
  - `bank_transfers`
  - `bank_disputes`
  - `bank_supporting_state`
- Splits core banking records into dedicated tables (`users`, `accounts`, `ledger_entries`, `transactions`, `transfers`, `disputes`)
- Stores each row as structured metadata columns plus a JSONB payload for model compatibility
- Uses a `SERIALIZABLE` transaction and `SELECT ... FOR UPDATE` row lock for each write
- Partitions data by `StateKey` (default: `bank_state`) so multiple environments can share one database safely
- Reads legacy single-row `bank_state` payloads and migrates them on the next write

If you need to force URL binding:
```bash
dotnet run --project src/AtmMachine.WebUI/AtmMachine.WebUI.csproj --urls http://127.0.0.1:5074
```

### Run Console App
```bash
dotnet run --project src/AtmMachine.ConsoleUI/AtmMachine.ConsoleUI.csproj
```

## Usage
Use this project for:
- Recruiter demos of secure banking-style workflows
- Portfolio showcase of layered backend + polished web UI
- Security-focused demos (lockout, audit, role-based admin)
- Transaction system demos with pending/posted settlement behavior

## Roadmap
- [ ] Move the remaining supporting collections (notifications, audit logs, login history, budgets, goals) into dedicated PostgreSQL tables
- [ ] Reduce JSONB payload dependence by querying directly from relational columns in the Postgres provider
- [ ] Add first-class P2P user-to-user transfers with DB transactions
- [ ] Add end-to-end tests for disputes/admin workflows
- [ ] Publish a stable hosted demo URL
- [ ] Add mobile client (Jetpack Compose) consuming `/api/v1`

## Contributing
Contributions are welcome.
- Fork the project
- Create your feature branch (`git checkout -b feature/AmazingFeature`)
- Commit your changes (`git commit -m 'Add some AmazingFeature'`)
- Push to branch (`git push origin feature/AmazingFeature`)
- Open a pull request

### Contributors
<a href="https://github.com/SpencerVJones/ConsoleATM/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=SpencerVJones/ConsoleATM"/>
</a>

## License
Distributed under the MIT License. See `LICENSE` for more information.

## Contact
Spencer Jones  
📧 [jonesspencer99@icloud.com](mailto:jonesspencer99@icloud.com)  
🔗 [GitHub Profile](https://github.com/SpencerVJones)  
🔗 [Project Repository](https://github.com/SpencerVJones/ConsoleATM)

---
If you rename this repository, update `SpencerVJones/ConsoleATM` in badge and link URLs.
