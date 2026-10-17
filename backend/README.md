# Omabilo Personal: standalone backend

A regular ASP.NET Core web API for Omabilo Personal that runs **without IKON**. It sits beside
the IKON app in the same repository and changes nothing in it.

## What it is, in plain terms

- The IKON app (`app/Omabilo`) draws its screens on the server and streams them to the browser
  through IKON. That needs IKON's runtime to sign people in, store data and stream the UI.
- This backend is a **JSON API** instead: a program that answers web requests such as
  "list my invoices" or "approve this invoice" with data. The website in `web/` uses it.
- `frontend-node` cannot use this API: it is the IKON app's viewer and only talks to an IKON server.

## What runs independently, and what was replaced

| Part | Status |
|---|---|
| Invoice rules: matching, duplicates, review, audit, export, delete | **Real.** The same code as the IKON app, linked from `app/Omabilo/Invoices` rather than copied. |
| Balance, transactions, loans, goals, payday plan, reports | **Real per-user records** (`/api/finance/...`). Each new user starts with fictional Finnish demo data (four months of history) that they can change. |
| Older read-only demo routes | `/api/demo/...` still exist; the website uses `/api/finance/...` instead |
| Sign-in (IKON auth) | **Replaced.** In Development, fictional users only (header `X-Demo-User: demo-alice`). Anywhere else it needs JWT tokens from an identity provider you configure, and refuses to start without one. |
| Data storage (IKON persistent state) | **Replaced.** In memory (tests), or a local SQLite file `App_Data/omabilo-dev.db` (git-ignored) |
| Email ingestion, AI document reading, Gmail/Outlook | **Not available.** Email and scans are simulated on fictional samples, exactly as in the IKON app. |
| Screens | None here. The website is in `web/`. |

## Folders

```
backend/
  Omabilo.Backend.slnx           solution: open this in an IDE
  src/Omabilo.Api/
    Program.cs                   startup: storage, sign-in, safety settings
    Auth/                        development-only fictional sign-in
    Storage/                     IInvoiceStore: InMemory and SQLite implementations
    Finance/                     finance records, Finnish demo seed, calculator, IFinanceStore
    Endpoints/                   the API routes
    appsettings.json             safe defaults (no secrets)
    appsettings.Development.json local settings: demo sign-in and SQLite
  tests/Omabilo.Api.Tests/       tests that start the API and call it over HTTP
```

The shared files are linked in `Omabilo.Api.csproj` (`<Compile Include="..\..\..\app\Omabilo\...">`).
Editing the invoice rules in `app/Omabilo/Invoices` changes both the IKON app and this API.

## Run it locally

You need the .NET 10 SDK.

```bash
cd backend
dotnet test
dotnet run --project src/Omabilo.Api
```

`dotnet test` runs the API tests. `dotnet run` starts the server on http://localhost:5080 in
Development mode. Then, from another terminal:

```bash
curl http://localhost:5080/health
curl -X POST -H "X-Demo-User: demo-alice" http://localhost:5080/api/invoices/demo
curl -H "X-Demo-User: demo-alice" http://localhost:5080/api/invoices
```

## API

All routes except `/health` need a signed-in user. The user is always taken from the validated
sign-in, never from the URL or body. Another user's invoice answers 404, the same as a missing one.

| Method and path | What it does |
|---|---|
| `GET /health` | Is the server up |
| `GET /api/invoices?status=unpaid\|review\|paid\|closed` | List my invoices |
| `GET /api/invoices/summary` | Counts and the unpaid total |
| `GET /api/invoices/{id}` | One invoice with its sources, history and reference check |
| `POST /api/invoices` | Add an invoice by hand |
| `PATCH /api/invoices/{id}` | Correct an invoice's fields |
| `POST /api/invoices/{id}/approve`, `/reject`, `/pay`, `/unpay`, `/keep-separate`, `/merge` | Review and payment actions |
| `POST /api/invoices/demo` | Load fictional demo invoices |
| `POST /api/invoices/simulate/email/{key}` | **Simulated** forwarded email (`email-energy`, `email-water`, `email-energy-again`, `email-insurance`) |
| `POST /api/invoices/simulate/document/{key}` | **Simulated** scan (`doc-dental`, `doc-poor-scan`) |
| `GET /api/invoices/export` | Download all my invoice data as JSON |
| `DELETE /api/invoices?confirm=delete-all-invoice-data` | Permanently delete all my invoice data |
| `GET /api/invoice-sources` | Which sources are available, simulated or not connected |
| `GET /api/demo/debts`, `/api/demo/debts/plan?strategy=&extra=` | Fictional debts and a payoff plan |
| `GET /api/demo/reports/{yyyy-MM}` | Fictional monthly report |
| `GET /api/finance/overview` | Dashboard: balance, payday, unpaid invoices, next 14 days, cash flow |
| `GET /api/finance/payday-plan?until=` | Payday plan with every line labelled Confirmed, Scheduled, Estimate or Planned, plus what was left out and why |
| `GET/PUT /api/finance/settings` | Name, payday, salary, daily spending plan |
| `GET /api/finance/transactions?month=`, `GET /api/finance/reports`, `GET /api/finance/reports/{yyyy-MM}` | Transactions and monthly reports (period and partial month labelled) |
| `GET /api/finance/debts`, `GET /api/finance/debts/plan`, `POST /api/finance/debts/{id}/payments` | Loans, payoff plan, logging a payment |
| `GET/POST /api/finance/goals`, `PATCH/DELETE /api/finance/goals/{id}`, `POST /api/finance/goals/{id}/contributions` | Savings goals; adding or withdrawing moves money in the account |
| `GET /api/finance/expected-income`, `PATCH /api/finance/expected-income/{id}` | Expected payments; only confirmed ones count |
| `POST /api/finance/reset-demo?confirm=reset-demo-data` | Fresh demo finances |

## Before it could run anywhere real

1. **Identity provider.** Set `Auth__Authority` and `Auth__Audience` as environment variables (never in a committed file). Any OpenID Connect provider that issues JWT access tokens works.
2. **Database.** SQLite suits one machine. A hosted deployment needs a server database (for example Postgres) behind `IInvoiceStore` and `IFinanceStore`. See `../DEPLOYMENT.md`.
3. **HTTPS and CORS.** HTTPS redirection and HSTS switch on outside Development. List your frontend's address in `Cors__AllowedOrigins__0`.

Nothing here deploys anything, and no hosting has been set up.
