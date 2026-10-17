# Project status (handoff)

Updated 17 Oct 2026. Read this first; details live in `DECISIONS.md` (why),
`DEPLOYMENT.md` (how to deploy) and `ARCHITECTURE.md` (invoice audit and security plan).

## Goal

Omabilo Personal ("Your money. Your way."): a mobile-friendly financial-wellbeing website at a
public link, backed by a standalone API, with a safe path to real invoice collection later.
**All data is fictional**; nothing connects to a bank, mailbox or provider.

## Architecture

| Folder | What it is | Status |
|---|---|---|
| `app/Omabilo/` | The original IKON app: C# screens streamed by IKON | Works locally. **Keep unchanged.** |
| `frontend-node/` | The IKON app's viewer; only works with an IKON server | **Keep unchanged**: `ikon build`/`run`/`deploy` depend on it |
| `backend/` | ASP.NET Core API (.NET 10), no IKON | Invoices plus per-user finances; sign-in and storage replaced (below) |
| `web/` | React + Vite + TypeScript website for Vercel; calls `backend/` only | 7 screens, honest offline and error states |
| `test/Omabilo.Test/` | Tests for the shared, IKON-free invoice code in `app/Omabilo/Invoices/` | |

## Done

- **Invoices:**
  - canonical model separate from source records
  - idempotent intake, duplicate matching (no automatic merge of ambiguous records), review
  - audit history without amounts or document text
  - export and confirmed delete-all
- **Finances:**
  - per-user records starting from 4 months of fictional Finnish demo data
  - one calculator for the dashboard, payday plan, debts, goals and reports
  - payday-plan lines labelled Confirmed, Scheduled, Estimate or Planned
  - no double counting; unconfirmed income isn't counted
- **Sign-in:** `X-Demo-User: demo-…` only in Development; JWT otherwise. The API refuses to start without it configured.
- **Storage:** in memory, or a local SQLite file (git-ignored).
- **Browser access (CORS):** only the exact origins in `Cors:AllowedOrigins`.
- **Website:**
  - Dashboard, Financial Inbox, Debt Tracker, Payday Planner, Savings Goals, Reports, Settings & Connections
  - `VITE_API_BASE_URL` only, never `localhost` or `http` in production
  - a connection indicator; no fake success
- **Financial Inbox user test (IKON app):** "Explain an invoice" tab. It analyses three fictional emails or a pasted one with fixed rules, not AI (`Invoices/InvoiceExplainer.cs`), then shows a 7-question feedback form (`Invoices/InvoiceLab.cs`).
  - Answers are saved in the app's built-in IKON database, table `omabilo_test_feedback`. Pasted text is never stored.
  - Review the answers with `ikon db query "select * from omabilo_test_feedback order by created_utc"`.
- **HTTPS proxy fix (uncommitted, see below):** forwarded headers are believed only from a configured proxy, and `/health` is never redirected.

## Test results

**Real automated tests (17 Oct 2026, current code):**
- `dotnet test backend/Omabilo.Backend.slnx`: **53/53 passed**. This includes 10 proxy tests run against a *simulated* proxy in the in-memory test server.
- `dotnet test test/Omabilo.Test`: **36/36 passed**.
- `npm --prefix web test`: **20/20 passed**. `npm run build` (type check + build): OK. `npm audit`: 0 vulnerabilities.
- IKON app build: 0 warnings, 0 errors.

**Manual and simulated checks, not automated:**
- Browser run of `web/` against a local API, on desktop and phone sizes: an import, a duplicate link, approve, pay (the balance dropped exactly once), and the API-down state (errors, no figures).
- Dockerfile build stage *simulated*: its exact files were published with `dotnet publish -c Release` in an empty folder. In Production mode it refused to start without sign-in, returned 401 without a token, and allowed only the configured CORS origin.

**Never tested:**
- a real `docker build`
- any deployment
- a real identity provider
- a hosted database
- screen-reader testing
- real email, AI or OAuth

## Git

- **Branch:** `main` at `cafa063` (IKON app polish: plain-language dashboard, payday breakdown, Smart Spend Guard), in step with IKON.
- **IKON (`origin`):** `cafa063`, saved with `ikon save` and **deployed** on 17 Oct 2026. The live app works for signed-out visitors, who get fresh fictional demo data.
- **GitHub (`github`):** `e624604` (pushed 17 Oct 2026), a noreply snapshot of `main` at `978817d` plus the then-uncommitted proxy fix, tests and docs. The local branch `github-main` tracks it.
- **Recent local commits:**
  - `1157de4`: standalone API
  - `eca76bf`: website + finance API
  - `978817d`: label tests + deploy notes
- **Uncommitted (keep):**
  - the proxy fix: `backend/src/Omabilo.Api/Program.cs`, `appsettings.json`, new `backend/tests/Omabilo.Api.Tests/ProxyTests.cs`
  - new web tests in `web/src/api/client.test.ts` and `web/src/config.test.ts`
  - doc updates in `DECISIONS.md`, `DEPLOYMENT.md` and this file
  - `frontend-node/package-lock.json`: npm churn, deliberately never committed
- **Publishing rules** (`DECISIONS.md`):
  - GitHub gets parentless or fast-forward **noreply snapshots** built on `github-main`, so the personal emails in local commits are never published.
  - Never push `main` to `github`, and never force-push.
  - `ikon save` commits everything; back up and restore the lockfile around it.

## Environment issues

- **Docker Desktop:** installed, but its engine doesn't start (`failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine`). WSL has no Docker distribution, so first-time setup was never completed. The Docker Desktop process exits after launch. **Impact:** no real image build or container test. **Fix:** you open Docker Desktop and finish its setup.
- **Windows Application Control:** it sometimes blocks a freshly rebuilt `Omabilo.Api.dll` ("An Application Control policy has blocked this file"), stopping `dotnet run` and, once, the tests. A later rebuild was allowed. It was never bypassed.
- **IKON:** deployed (deployment hash `f7d7d94e`, about 20 credits available before deploying). `ikon browse --as-guest` is refused for this space ("shared session needs a developer"), but a real signed-out browser loads the app.
- **Vercel:** the existing project still builds `frontend-node`, so it shows "Connecting…". Its Root Directory must become `web`.

## HTTPS proxy issue

- **Problem:** hosts end HTTPS at their proxy and forward plain HTTP. Before the fix the API redirected every request, which broke browser CORS checks.
- **Fixed in code (uncommitted):**
  - `UseForwardedHeaders` trusts only `Proxy:TrustAllProxies` (private container port) or `Proxy:KnownNetworks`.
  - Untrusted headers are ignored.
  - `ASPNETCORE_HTTPS_PORT=443` sets the redirect target.
- **Still unverifiable locally:** what a real host sends and its proxy's address range. Choose the setting from the host's documentation (`DEPLOYMENT.md`).

## Remaining blockers before a public link

1. **Identity provider** (your choice; free tiers need your account). Then the website needs a real sign-in flow that sends `Authorization: Bearer`; today it only sends `X-Demo-User`.
2. **Hosted database:** a Postgres store behind `IInvoiceStore`/`IFinanceStore` (your choice of host). SQLite and in-memory storage are not durable.
3. **A real Docker build** and a container host choice; set the host's proxy setting.
4. **Security before any real data** (`DEPLOYMENT.md` and `ARCHITECTURE.md`): encryption at rest, private document storage, tested backup and restore, account deletion, retention.
5. **Invoice integrations:** only manual, demo and *simulated* email and scans. Gmail, Outlook and Finnish e-invoices are "Not connected". Real forwarding, AI extraction and OAuth need credentials, credits and consent flows (`ARCHITECTURE.md` phases 2–4).

## Single best next action

Commit the uncommitted proxy fix and its tests locally (leaving out `frontend-node/package-lock.json`),
so the verified work is safe in Git. Then decide the identity provider and the Postgres host.
