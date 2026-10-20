# Decisions

Each entry is a choice that would be costly to undo or rediscover.

## Platform and hosting

- **IKON is the runtime and the host.** Screens are C# streamed by the IKON server; `frontend-node` is only a viewer for it. Static hosts (Vercel, Netlify, GitHub Pages) cannot run the app.
- **One instance per user** (`SessionIdentity(UserId)`) in the cloud; a local run is one shared instance.
- **Fictional data only** until the Phase 2 security items in `ARCHITECTURE.md` are done. *Superseded 17 Oct 2026 for the connected companion below: real Gmail data is in scope, for signed-in users who connect it.*

## Invoices

- **Source records stay separate from canonical invoices.**
  - **Idempotency key:** a hash of owner, source type and source id. The same key is never ingested twice.
  - **Exact match** (the same payment reference, or the same provider and invoice number): the new source links to the existing invoice.
  - **Probable match** (same provider and amount, due within 3 days): the owner chooses Merge or Keep both.
  - Only `Ready` and `Verified` invoices count in totals.
- **Who reviews:** manual and demo invoices are confirmed at once. Anything read from a source waits for review.
- **Audit and logs never contain amounts, references, document text or tokens.**
- **Storage:** Phase 1 uses `PersistentUserReactive` lists (user-scoped, and erased by the platform with the account). Phase 2 moves to Postgres tables, because IKON's `EmailReceived` trigger and `[Cron]` jobs run outside any user's instance.
- **Approved invoices appear in the Financial Inbox** as one bill (`LinkedBillId`), and payment stays in sync both ways. Demo invoices use providers the Inbox demo bills don't, so nothing is counted twice.
- **The domain files in `app/Omabilo/Invoices` stay free of IKON types.** `test/` and `backend/` link them with `<Compile Include … Link>` rather than copying them.

## Standalone backend

- **`backend/` reuses the shared domain and replaces IKON services:**
  - **Sign-in:** demo sign-in only in Development, with `demo-…` ids. Otherwise JWT, and it refuses to start unconfigured.
  - **Storage:** in memory, or local SQLite (git-ignored).
- It doesn't replace the IKON app. The other screens' data isn't ported yet.

## Standalone website and finance API (chosen 17 Oct 2026)

- **The website lives in `web/`**, not in `frontend-node/`, because converting `frontend-node` would break `ikon build`/`run`/`deploy`. Vercel's Root Directory must point at `web`.
- **One per-user finance document** (`FinanceData`: settings, transactions, recurring items, expected income, loans, goals) behind `IFinanceStore`. `FinanceCalculator` is the only place money is computed, so every screen agrees.
- **Payday-plan rules:**
  - The plan runs from today to the day before payday. Salary is never counted before it arrives.
  - Expected income counts only once the person confirms it.
  - Each line is labelled Confirmed, Scheduled, Estimate or Planned. Anything left out is listed with the reason.
- **No double counting:**
  - A recurring bill is skipped when it was already paid this month, or when an unpaid confirmed invoice from the same provider is due within 7 days of it. The invoice counts instead.
  - Paying an invoice records one transaction linked to it, and un-paying removes it.
- **The website never shows fake success or fallback data.** Failures show an error with Retry, and the API address comes only from `VITE_API_BASE_URL`, which can never be `localhost` or `http` in production.

## Hosting proxy (chosen 17 Oct 2026)

- **The API believes `X-Forwarded-Proto`/`-For` only from proxies a deployment names:**
  - `Proxy:TrustAllProxies`, only when the container port is private
  - or `Proxy:KnownNetworks` CIDRs

  The default trusts nothing outside the container, because a trusted fake header could make plain HTTP look secure. `/health` is exempt from the HTTPS redirect for plain-HTTP health checks.

## Connected financial companion (chosen 17 Oct 2026)

- **Built into the IKON app (`app/Omabilo/Companion/`), not `backend/` + `web/`.**
  - **Sign-in:** IKON's own (Google, email codes, passkeys, logout, account removal); the standalone pair has no identity provider.
  - **Other services:** a Postgres database, stored secrets, `[Cron]` jobs that run without a browser, stable public `/api` callback addresses, and EU-pinned AI.
  - `backend/` and `web/` stay as they are.
- **Signing in and connecting Gmail are separate.** Only a signed-in, non-guest user can start the Gmail flow, and connecting is its own explicit step with its own consent screen.
- **Gmail access:**
  - The only scope is `gmail.readonly`. It's restricted, so Google verification is needed before the app leaves Testing mode.
  - OAuth uses the code flow with PKCE and a one-time `state` row (10 minutes). The callback is `/api/oauth/google/callback`.
  - Tokens are encrypted with AES-256-GCM (key in the IKON secret `OMABILO_TOKEN_KEY`, user id as associated data) and live only in Postgres.
- **What is read and kept:**
  - Metadata and snippet first; the full message only when it may be financial.
  - Only an excerpt (at most 800 characters) is stored per financial message, never whole bodies.
  - Non-financial messages keep just their id, so they aren't fetched again.
- **Sync:**
  - A `[Cron]` sweep every 15 minutes, plus "Sync now".
  - The first scan is bounded (90 days and 300 messages by default, both configurable). After that, sync is incremental using Gmail's history ids, with a bounded rescan when the history has expired.
  - A per-user claim column prevents overlapping runs. Failures back off exponentially.
- **Forwarding is the way in for everyone (chosen 19 Oct 2026).** Google allows the restricted `gmail.readonly` scope only for listed testers until the app passes verification and a yearly CASA audit, so it can't serve people whose addresses the owner doesn't know.
  - Each signed-in user gets a private address `bills-<12 random chars>@<app hostname>` (IKON inbound email, `ikon inbox enable`). They forward bills by hand or with a Gmail/Outlook rule. Works with any provider, and gives Omabilo no mailbox access.
  - The address alone decides whose data a message becomes; mail to an unknown or replaced address is deleted unread. "Get a new address" replaces a leaked one.
  - Forwarded mail goes through the same classifier, extractor and filing as Gmail mail, with ids prefixed `fwd:`. The platform's copy is deleted after reading; only the extracted record and excerpt stay, so forwarded items can't be re-read.
  - Gmail's forwarding-confirmation email is shown to the person (code, and a link only if it points at `mail.google.com`/`mail-settings.google.com`), never filed.
  - Connect Gmail stays for testers.
- **Numbers come from code, never from AI.** Extraction and cash flow are deterministic and tested. AI (opt-in per user, EU-pinned, redacted input) only explains, and its output is validated: a fact naming a number that isn't in the source is dropped.
- **Statuses never overclaim:**
  - "Overdue" means the due date has passed with no payment reported or confirmed. It doesn't mean verified unpaid.
  - "Paid" is either reported by the user or confirmed by a matching payment-confirmation email.
- **Merging duplicates:** invoices merge on the same invoice number; on the same reference only when due within 20 days. Otherwise a likely duplicate is flagged, not merged, because Finnish rent and recurring bills reuse one reference.

## Git

- **`origin`** is the IKON repo and is updated by `ikon save`, which commits every change.
- **`github`** is the public mirror. It gets **parentless snapshot commits** on the local branch `github-main`, authored as `143219539+MahbobaAkter@users.noreply.github.com`, so personal email addresses in `main`'s history are never published.
  - Build each snapshot from `main`'s tree and push it to `github` `main` without force.
  - Never push `main` itself to `github`.
- **`package-lock.json` changes from `npm install`** are kept out of commits. Before `ikon save`, back the file up, restore the committed copy, save, then put it back.
