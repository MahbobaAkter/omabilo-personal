# Omabilo Personal: architecture audit and invoice plan

Audit date: 17 Oct 2026. Every capability below was checked in the code or in the IKON docs
shipped with this project (`docs/`). "Not documented" means no evidence was found, not that the
feature is absent.

## 1. Current verified architecture

| Part | What it is |
|---|---|
| Server | One C# IKON app (`app/Omabilo`, .NET 10, IKON 3.16.9). All screens, logic and data live here. |
| Frontend | `frontend-node`: a Vite/React thin client (`<IkonApp>`) that renders the UI the server streams. No app logic. |
| Identity | `SessionIdentity(UserId)`: in the cloud, each user (and each guest browser) gets its own server instance. A local run is one shared instance. |
| Sign-in | IKON auth: Google, Apple, Microsoft, Signicat, email code, passkey, guest. `RequireSignIn = false`, so guests can use the app. |
| Screens | Dashboard, Financial Inbox, **Invoices (new)**, Debt Tracker, Payday Planner, Savings & Goals, Monthly Reports. There is no separate settings screen; "Pay details" and "Reset demo data" are dialogs. |
| Calculations | `Metrics.cs` (balance, safe-to-spend, wellbeing score), `Fin.Simulate` (avalanche/snowball payoff), report aggregation in `Reports.cs`. All real logic over the stored records. |

**Persistence.** Existing records (transactions, inbox, debts, goals, pay plan) are
`PersistentSessionReactive*` collections: durable, one set per `SessionIdentity`, written to the
app's built-in EU Postgres state table about 2 s after each change. They start as **fictional demo
data** and are fully editable. New invoice records use `PersistentUserReactive*` (see Phase 1).

**What is simulated.** All starting data is fictional. No bank, mailbox or provider is
connected. In Invoices, the "Forwarded email" and "PDF or photo" sources run real parsing code
over fictional synthetic text and are labelled **Simulated**. Gmail, Outlook and Finnish e-invoices
are shown as **Not connected**.

**Tests and build.** There were no tests before this phase. The app now builds with 0 warnings,
and `test/Omabilo.Test` has 36 passing unit tests.

## 2. Gaps and risks

1. **Guests can hold data.** With `guest` sign-in, a person's records belong to one browser; clearing it loses them. Real invoices need `RequireSignIn = true` and no `guest`.
2. **Session-scoped data is not erased with the account.** The platform erases `PersistentUser*` values on account erasure, but not `PersistentSession*` ones. The existing screens use session scope.
3. **Persistent state is not a records database.** IKON's own guidance says records people create belong in Postgres tables. Persistent values are last-writer-wins, and a crash loses the last ~2 s.
4. **Encryption at rest is not documented** for the database, files or persistent state; only the inbound email store is stated as encrypted. **Backups and point-in-time restore are not documented** either. Both need confirmation from IKON before storing real invoices.
5. **Private files are not per-user access-controlled.** Signed URLs work for anyone holding them for one hour; privacy comes from the app only ever showing a user their own paths.
6. **No per-user secret store.** `app.Secrets` is app-wide and read-only. OAuth refresh tokens for Gmail and Outlook would have to be stored by the app itself.
7. **Microsoft token revocation is not documented** (Google has `GoogleAuth.RevokeAsync`).
8. **AI extraction needs credits.** The organisation has 0 credits, so `Emerge`/OCR calls are denied. The deploy is also waiting on credits.
9. **A local run uses production data.** `ikon run` reads and writes the deployed app's database and files. Real-data work should use a staging copy (`ikon link --target staging --new-app`).
10. **Background work runs outside the user.** `[Cron]` ticks and the `EmailReceived` trigger run in the userless instance, so they can only reach per-user data through database rows or `…For(userId)` accessors.

## 3. Recommended architecture (IKON only, no new providers)

**Canonical invoice model** (`app/Omabilo/Invoices/InvoiceModel.cs`)

- `InvoiceSourceRecord`: one arrival from one source.
  - Fields: owner, source type, source id, idempotency key (a hash of owner, type and source id), simulated flag, extracted fields with per-field confidence, received time, and the linked invoice.
- `CanonicalInvoice`: one per real bill.
  - Identity and payment details: internal id, owner, provider, invoice number, reference, invoice and due dates, amount, currency, payment status.
  - Review and processing: verification, processing state (NeedsReview, Ready, Rejected, Merged), confidence, primary source, source record ids, created and updated times.
  - Links: possible-duplicate link, merged-into link, linked Inbox bill.
- `InvoiceAuditEvent`: who did what and when. It names fields and states but never amounts, document text or credentials.

**Matching, without double counting** (`InvoiceBook.cs`)

- The same idempotency key means the source was already received, so nothing changes.
- The same payment reference, or the same provider plus invoice number, is an exact match: the new source is linked to the existing invoice.
- The same provider and amount with due dates within 3 days is a probable match: the invoice is held for review, and the owner chooses Merge or Keep both.
- Only Ready and Verified invoices count in totals.

**Source interfaces** (`InvoiceSources.cs`)

| Interface | Phase 1 implementation |
|---|---|
| `IInvoiceSource` (catalog entry) | All six sources, each with Available, Simulated or Not connected status |
| Manual/demo input | `ManualInvoiceSource`, plus fictional demo invoices |
| `IDocumentExtractor` (PDF/image) | `TextInvoiceExtractor` reads Finnish and English invoice text and validates references. **Simulated** on synthetic scans. |
| Forwarded email | `EmailInvoiceReader` turns an `InboundEmail` into submissions. **Simulated** on synthetic emails. |
| `IMailboxInvoiceSource` (Gmail, Outlook) | Contract only, Not connected |
| `IProviderInvoiceSource` (Finnish e-invoices) | Contract only, Not connected |

**Target storage (Phase 2).** Use built-in Postgres tables with an `owner_id` on every row: `invoice`, `invoice_source` (unique on owner and idempotency key), `invoice_audit` and `connector_account`. Documents go in `app.Files.Data` under `users/<id>/invoices/<sha256>`. Phase 1 keeps the same records in `PersistentUserReactive*` lists so the model can be proven first.

## 4. Security design

| Need | Design on IKON |
|---|---|
| Authenticated, per-user access | Require sign-in for invoices (no guest). The owner is always `ReactiveScope.UserId`, read on the server and never sent by the browser. |
| Server-side authorization | Every `InvoiceBook` read filters by owner, and every write calls `Owned(owner, id)`. Someone else's record gets the same "not found" answer as a missing one. In Phase 2 every SQL statement carries `WHERE owner_id = @owner`. |
| OAuth tokens | Use the app's own OAuth client via `Ikon.Connectors.Google`/`.Microsoft` with PKCE. The client secret goes in `app.Secrets` (`ikon secret set`). Refresh tokens go in a `connector_account` row, encrypted with an app-held key from `app.Secrets` (AES-GCM) because platform encryption at rest is not documented. Rotated tokens are saved through `onRefreshTokenRotated`. Disconnect deletes the row and calls `GoogleAuth.RevokeAsync`; Microsoft has no documented revoke, so the UI tells the user to remove access in their account. |
| Private documents | `app.Files.Data` under `users/<id>/…`, stored by `AssetUri` and never by URL. URLs are signed per view and expire in an hour. |
| Encryption in transit | HTTPS on `*.ikonai.app`. In-app encryption of tokens as above. At-rest encryption of the database and files must be confirmed with IKON. |
| Export | **Done:** "Export invoice data" downloads JSON of the owner's invoices, sources and history. |
| Deletion | **Done:** "Delete all invoice data" permanently deletes after a confirm. Phase 2 adds a `[Trigger(TriggerEventType.UserErased)]` handler that deletes rows, files and tokens idempotently, plus `app.EraseUserStateAsync`. |
| Retention | Source documents expire through `AssetMetadata(expiresAt)` after a set period (proposed: 24 months). Audit events, which hold no contents, are kept until account deletion. |
| Audit | `InvoiceAuditEvent` for every create, link, approve, reject, edit, merge, payment, undo and deletion. |
| Backups and recovery | Not documented. Ask IKON before storing real invoices; meanwhile `ikon export` gives a manual export. |
| Safe logging | Log ids, counts and outcomes only (as in `IngestAsync`). Never log amounts, references, document text, email bodies or tokens. Wrap unavoidable values in `Log.Sensitive`. Never log a `ConnectorException.Reason` or the database connection string. |

## 5. Phased plan

| Phase | Scope |
|---|---|
| **1 (done)** | Canonical model, matching and idempotency, audit, source interfaces, simulated email and document sources, review queue, the Invoices screen, Inbox bill sync, export and delete, unit tests |
| 2 | Postgres tables and migration from Phase 1 lists; require sign-in; erasure trigger; a staging app; real forwarding with `ikon inbox enable` plus an `EmailReceived` trigger that maps recipient to user; real uploads to `app.Files.Data`; AI extraction once credits exist (tested with `--mock-ai`) |
| 3 | Gmail and Outlook through the user's consent (`app.Consent` plus OAuth), encrypted token storage, revoke on disconnect, and delta sync from a `[Cron]` job reading `connector_account` rows |
| 4 | Finnish e-invoice and biller integrations behind `IProviderInvoiceSource` |

## 6. Phase 1 files

New:
- `app/Omabilo/Invoices/InvoiceModel.cs`: model, idempotency keys, Finnish and RF reference checks
- `app/Omabilo/Invoices/InvoiceBook.cs`: ingestion, matching, review, merge, payment, deletion, audit, owner checks
- `app/Omabilo/Invoices/InvoiceSources.cs`: source interfaces, the catalog, the text extractor, the email reader
- `app/Omabilo/Invoices/SyntheticInvoices.cs`: fictional emails, scans and demo invoices
- `app/Omabilo/Invoices/InvoiceService.cs`: user-scoped storage, Undo, Inbox bill sync, export and deletion
- `app/Omabilo/Invoices/InvoicesScreen.cs`: the Invoices screen, review queue, detail sheet and dialogs
- `test/Omabilo.Test/*`: the unit tests

Changed:
- `OmabiloApp.cs`: the Invoices navigation entry and badge
- `Metrics.cs` and `Inbox.cs`: paying or reopening a bill keeps its invoice in step

## 7. Tests

Run `dotnet test test/Omabilo.Test`. The 36 tests cover:
- idempotent re-ingestion
- exact matching by reference and by invoice number
- probable duplicates, and merging them without double counting
- review gating, manual and demo auto-confirm, and invalid sources
- owner isolation for reads, writes and matching
- owner-only deletion
- audit entries without contents
- idempotency key scope
- Finnish and RF reference validation
- Finnish and English amount and date parsing
- poor-scan handling
- the email reader and duplicate-email matching
- that all samples are fictional and every connector is not connected

Checked end to end in the running app with `ikon browse`:
- loading demo invoices and simulating emails and scans
- linking a duplicate and editing an invoice
- the detail sheet's sources and history
- deleting all invoice data
- Inbox ↔ invoice payment sync
- every existing screen
