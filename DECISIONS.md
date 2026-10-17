# Decisions

Each entry is a choice that would be costly to undo or rediscover.

## Platform and hosting

- **IKON is the runtime and the host.** Screens are C# streamed by the IKON server; `frontend-node` is only a viewer for it. Static hosts (Vercel, Netlify, GitHub Pages) cannot run the app.
- **One instance per user** (`SessionIdentity(UserId)`) in the cloud; a local run is one shared instance.
- **Fictional data only** until the Phase 2 security items in `ARCHITECTURE.md` are done.

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

## Git

- **`origin`** is the IKON repo and is updated by `ikon save`, which commits every change.
- **`github`** is the public mirror. It gets **parentless snapshot commits** on the local branch `github-main`, authored as `143219539+MahbobaAkter@users.noreply.github.com`, so personal email addresses in `main`'s history are never published.
  - Build each snapshot from `main`'s tree and push it to `github` `main` without force.
  - Never push `main` itself to `github`.
- **`package-lock.json` changes from `npm install`** are kept out of commits. Before `ikon save`, back the file up, restore the committed copy, save, then put it back.
