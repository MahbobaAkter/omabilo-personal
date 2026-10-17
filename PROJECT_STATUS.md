# Project status

Last updated: 17 Oct 2026. Everything marked "verified" was checked on that date.

## Status at a glance

There are three independent pieces, none of them live yet:
- **IKON app:** works locally. Deployment is blocked on IKON credits.
- **Standalone API + website:** work together locally; verified in a browser on desktop and phone sizes.
- **Public website link:** blocked on an identity-provider choice and a hosted database (see `DEPLOYMENT.md`).

All data is fictional.

## Implemented

- **IKON app (`app/Omabilo`)**, 7 screens on fictional data, including Phase 1 invoices (details: `ARCHITECTURE.md`).
- **Standalone API (`backend/`):**
  - invoices: canonical model, idempotent intake, duplicate matching, review, audit, export, delete
  - per-user finances, starting from fictional Finnish demo data covering 4 months:
    - balance, transactions, loans and payments, goals and contributions
    - expected income needing confirmation
    - payday plan with labelled certainty, dashboard overview, monthly reports
  - sign-in: fictional demo sign-in in Development; JWT otherwise, refusing to start unconfigured
  - storage: in memory or SQLite (both local only)
- **Website (`web/`):** React + Vite + TypeScript with seven screens:
  - Dashboard
  - Financial Inbox: filters, a detail panel, review, merge, paid/unpaid, editing, simulated imports, export, confirmed delete
  - Debt Tracker
  - Payday Planner
  - Savings Goals
  - Reports
  - Settings & Connections

  Plus a server connection indicator and honest loading, empty, offline and error states.
- **Deployment prep:** `backend/Dockerfile` (untested), `web/vercel.json`, `DEPLOYMENT.md`.

## Uncommitted work (on `main`, after `1157de4`)

- **New:** `web/`, `backend/src/Omabilo.Api/Finance/`, the finance endpoints and tests, `backend/Dockerfile`, `DEPLOYMENT.md`, `DECISIONS.md`, `PROJECT_STATUS.md`
- **Changed:** `backend/` invoice pay/unpay routes, `Program.cs`, `appsettings.Development.json` (local CORS), `backend/README.md`, `CLAUDE.md`, `.gitignore`
- **Still uncommitted:** `frontend-node/package-lock.json` (npm churn, left out on purpose)

## Last verified (17 Oct 2026)

- `dotnet test backend/Omabilo.Backend.slnx`: 39 passed, before the last one-line change (the Finnish-style "18,00 €" label in `FinanceCalculator.cs`). Since that rebuild, Windows Application Control blocks the new `Omabilo.Api.dll`, so the suite can't load on this PC. **That line has no automated test result.**
- `dotnet test test/Omabilo.Test`: 36 passed.
- `npm --prefix web test`: 15 passed.
- `npm --prefix web run build`: type-check and build OK. The configured API address is built in, and no `localhost` address is.
- IKON app build: 0 warnings, 0 errors.
- In the browser against the running API:
  - every screen loads
  - an import and a duplicate link worked; approve and pay worked, and the balance dropped exactly once (812,40 → 754,20 €)
  - the payday-plan numbers match the hand-checked tests
  - the phone layout and menu work
  - with the API stopped, every screen shows "Can't reach the server" and no figures
- CORS: the website's exact origin is allowed; another origin is refused.
- **Untested:**
  - the Docker image build (Docker Desktop wasn't running)
  - any deployment
  - real sign-in
  - a hosted database
  - screen-reader testing
  - real email, AI or OAuth

## Known bugs and blockers

- **Public link:** needs an identity provider and a Postgres store behind `IInvoiceStore`/`IFinanceStore`. Both need your decision on providers.
- **Windows Application Control** blocked `dotnet run` of a freshly rebuilt API on this PC ("An Application Control policy has blocked this file"). Tests still ran. It wasn't bypassed; ask the device owner, or use Docker.
- **IKON:** 0 credits; the live IKON URL returns 404. IKON is still at `26930ef` (local `main` is ahead).
- **Vercel:** the existing project still builds `frontend-node`, so it shows "Connecting…" until its Root Directory is changed to `web` and `VITE_API_BASE_URL` points at a deployed API.
- **Two separate data models:** the IKON app's data (IKON state) and the API's data (SQLite/memory) don't sync.

## Git

- **`main`** is at `1157de4`.
- **IKON (`origin`):** `26930ef`.
- **GitHub (`github`):** `c8c1922` (a noreply snapshot of `1157de4`). Snapshot procedure: `DECISIONS.md`.

## Next three tasks

1. Review this work, then commit locally (and later push a noreply snapshot), each with your approval.
2. Decide the identity provider and the Postgres host, then write the Postgres stores and JWT setup and test them on a staging host.
3. Start Docker Desktop, build and run `backend/Dockerfile` locally, then deploy the API and point Vercel's `web/` project at it.
