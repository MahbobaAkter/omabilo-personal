# Project status (handoff)

Updated 17 Oct 2026 (PDF invoice processing). Read this first; details live in `DECISIONS.md` (why),
`DEPLOYMENT.md` (how to deploy) and `ARCHITECTURE.md` (invoice audit and security plan).

## Goal

Omabilo Personal ("Your money. Your way."): a mobile-friendly financial companion.
- **Signed-in people** connect Gmail (read-only). Omabilo then finds their bills, salary and receipts, explains them and plans their cash flow until payday. Gmail works once you set up the Google OAuth client (`DEPLOYMENT.md`).
- **The Demo screens** stay fictional.
- Nothing connects to a bank.

## Architecture

| Folder | What it is | Status |
|---|---|---|
| `app/Omabilo/` | The IKON app: C# screens streamed by IKON. **This is the live product.** | Demo screens plus the connected companion |
| `app/Omabilo/Companion/` | IKON-free companion logic: classification, extraction, cash flow, Gmail client, sync engine, Postgres store, AI guard | Tested in `test/Omabilo.Test` |
| `frontend-node/` | The IKON app's viewer; only works with an IKON server | **Keep unchanged**: `ikon build`/`run`/`deploy` depend on it |
| `backend/` | ASP.NET Core API (.NET 10), no IKON | Invoices plus per-user finances; sign-in and storage replaced (below) |
| `web/` | React + Vite + TypeScript website for Vercel; calls `backend/` only | 7 screens, honest offline and error states |
| `test/Omabilo.Test/` | Tests for the IKON-free code in `app/Omabilo/Invoices/` and `app/Omabilo/Companion/` (fake Google, in-memory store) | 114 tests |

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
- **Connected companion (IKON app, "Your money"):**
  - **Sign-in and access:** IKON sign-in (Google, email code, passkey, guest). Gmail connects only as a separate, explicit step: OAuth code flow with PKCE and a one-time state, scope `gmail.readonly`, tokens encrypted with AES-256-GCM.
  - **Gmail push, verified live on 17 Oct 2026:** the watch started at 16:47 UTC. Three new messages (16:48–16:52) were each processed about 18 s after arrival from OIDC-authenticated Pub/Sub notices, with no Sync now and the hourly fallback not due. A reminder merged into its existing record, one message was kept as not financial, and a receipt was created. The records from emails sent from a personal account showed the sender's name as the company; fixed by the PDF and issuer work below.
  - **Gmail push design:** `POST /api/gmail/push` accepts Pub/Sub notices (signed OIDC token or URL token), deduplicates them, and syncs only the named mailbox. Watches start after a sync, renew daily, and stop on disconnect. It needs the IKON secrets `GMAIL_PUBSUB_TOPIC` plus `GMAIL_PUSH_SERVICE_ACCOUNT` or `GMAIL_PUSH_TOKEN`; until then mailboxes are polled every 5 minutes.
  - **Sync:** a server-side sweep every 5 minutes (`[Cron]`; each mailbox every 5 minutes without push, hourly with an active watch) plus "Sync now". The first scan is bounded (90 days, 300 messages). After that, sync is incremental, with a rescan when Gmail's history expires, retries with backoff and a per-user lock.
  - **Reading mail:**
    - rule-based classification (English and Finnish)
    - extraction for invoices, reminders, collection notices, payslips, receipts and refunds
    - duplicate and reminder merging; payment confirmations mark bills paid
  - **PDF invoices (`Companion/InvoiceAnalysis.cs`, `PdfAttachmentReader.cs`, added 17 Oct 2026):**
    - attachments are downloaded with the Gmail attachment API (or taken from the message when Gmail inlines them), read with PdfPig in reading order, and label rows are paired with value rows
    - an email whose only content is a PDF is no longer dropped on its subject
    - each attachment's outcome is saved: read, read by OCR, scanned (needs OCR), password-protected, damaged, too large, not supported, download failed or timed out. Passing failures retry 3 times (15 min doubling)
    - OCR (Azure Document Intelligence, EU) only for people who turned on AI reading
    - fields saved with their source and confidence: company, amount, VAT, invoice and due dates, invoice number, reference, IBAN (check digits verified), purpose
    - the issuer comes from a labelled biller, a forwarded email's original sender, or the company name on the PDF, never from a personal or webmail sender's name
    - conflicts between the email and its PDF are flagged; two different invoices in one email become two items
    - plain-English, rule-based advice on the invoice screen (who, what, how much, when, status, next steps, checks), labelled "No AI"
    - items read by older rules are re-read once automatically, keeping the person's edits
  - **Planning:** deterministic cash flow until payday (Finnish weekend paydays move to Friday), with each line labelled You entered, From email or Estimate. Suggestions explain why they were made.
  - **Month by month** (`Companion/MonthlySummary.cs`): six months of bills due, bills marked paid, receipts, refunds, payslip net pay and pay reported received, plus a salary history. Duplicates and "not for me" records never count; a payslip is never counted as money received.
  - **Health:** `GET /api/health` (public, no personal data) reports the database, which secrets are configured (yes/no only), the exact Google redirect URI this server uses, the scope, and the scheduled sync's last run. That run is recorded per server address in `om_job_runs`, so a local run never passes for the live scheduler.
  - **AI:** opt-in only, EU-pinned Claude Haiku with redacted input. Output is validated; facts with unsupported numbers are dropped. If AI fails, the rule-based result stays.
  - **Controls:** correct, review, report paid, dispute, delete, re-read, export, delete all data, delete account (platform), and the `UserErased` handler.
  - **Storage:** Postgres tables `om_*` in the app's built-in database, every query scoped by user id.
- **HTTPS proxy fix (uncommitted, see below):** forwarded headers are believed only from a configured proxy, and `/health` is never redirected.

## Test results

**Real automated tests (17 Oct 2026, current code):**
- `dotnet test backend/Omabilo.Backend.slnx`: **53/53 passed**. This includes 10 proxy tests run against a *simulated* proxy in the in-memory test server.
- `dotnet test test/Omabilo.Test`: **117/117 passed** (17 Oct 2026, after the monthly summary and health check). This includes the companion: OAuth flow, sync, retries, isolation, AI fallback, PDF reading and cash flow, all run against a *fake* Google.
- `npm --prefix web test`: **20/20 passed**. `npm run build` (type check + build): OK. `npm audit`: 0 vulnerabilities.
- IKON app build: 0 warnings, 0 errors.

**Manual and simulated checks, not automated:**
- Browser run of `web/` against a local API, on desktop and phone sizes: an import, a duplicate link, approve, pay (the balance dropped exactly once), and the API-down state (errors, no figures).
- Dockerfile build stage *simulated*: its exact files were published with `dotnet publish -c Release` in an empty folder. In Production mode it refused to start without sign-in, returned 401 without a token, and allowed only the configured CORS origin.

**Companion, checked live in a local run against IKON's real Postgres (17 Oct 2026):**
- the signed-in, guest and second-user (isolation) views
- saving plan numbers and the cash-flow arithmetic
- review, report paid, correction with validation, delete, export, AI opt-in saved
- the callback refusing a forged state, and the cron job registered and running
- Test records were deleted afterwards.

**Never tested:**
- a real `docker build`, or a deployment of the standalone `backend/` + `web/`
- **financial extraction on real Gmail:** live OAuth now works (verified 17 Oct 2026 15:46 UTC with the dedicated test inbox: status Connected, only `gmail.readonly` granted, both tokens stored encrypted, first scan done, 0 failures, another account still sees "Connect Gmail"). The test inbox held one non-financial message, so no real financial record has been extracted yet. The first failed attempt was the Gmail API being disabled in the client's project (403 `accessNotConfigured`); the callback now says so.
- a **real AI call:** AI is only tested with a fake model
- **the live scheduler is verified:** after deploying `7d647f6`, `/api/health` showed the job ran on the live server at 2026-10-17 15:00:03 UTC (result: skipped, Gmail not configured). `ikon cron list/run --app-id` still wrongly says "no active deployment", which is a tool problem, not the app.
- **a published privacy policy:** `/legal/privacy` currently serves the app shell, not a policy. `PRIVACY_DRAFT.md` needs the owner's details before it can become `legal/privacy.md`.
- OCR for scanned PDFs (not implemented)
- screen-reader testing

## Git

- **Branch:** `main` at `f797189` (connected companion), saved to IKON and **deployed** on 17 Oct 2026. The monthly summary and `/api/health` are newer and not yet committed or deployed.
- **GitHub (`github`):** `e624604` (pushed 17 Oct 2026), a noreply snapshot of `main` at `978817d` plus the then-uncommitted proxy fix, tests and docs. The local branch `github-main` tracks it.
- **Recent local commits:**
  - `1157de4`: standalone API
  - `eca76bf`: website + finance API
  - `978817d`: label tests + deploy notes
- **Uncommitted:** the monthly summary, `/api/health`, their tests and these doc updates. `frontend-node/package-lock.json` churn is deliberately never committed.
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

Create the Google OAuth client and set its two IKON secrets (`DEPLOYMENT.md`, "Connected companion on
IKON"). Then deploy, check `https://omabilo-2143f1e7.ikonai.app/api/health` shows
`googleOAuthConfigured: true`, and connect the dedicated test inbox.
