# Omabilo V1 roadmap

Written 19 Oct 2026 from `Omabilo_Master_Product_Blueprint` v1.1 and the current code. Statuses use the
blueprint's words: VERIFIED WORKING, PARTIALLY IMPLEMENTED, UI/DEMO ONLY, MISSING, BLOCKED.

## Architecture decision (proposed, needs the owner's yes)

V1 is **one product: the IKON app** (`app/Omabilo/`). It has the live sign-in, the Postgres tables
(`om_*`, every query scoped by user id), Gmail, PDF reading, upload and the cron sync.
`backend/` + `web/` (Vercel) are frozen as a later option, not built in parallel.

```
Browser ── IKON thin client (frontend-node, unchanged)
              │ streamed UI
IKON app (C#) ── screens: Home · My inbox · Bills · Pay · Debts · Goals · Tax · Ask · Settings
   │  Companion/ (plain C#, unit tested): intake → read PDF → classify → extract → validate
   │                                     → review → save → explain → plan
   ├─ Postgres om_* (owner-scoped)      ├─ AI gateway: EU Claude Haiku, opt-in, schema-checked
   └─ Intake: upload · Gmail (read-only) · forwarding address
```

## Where we are (by module)

| Module | Status |
|---|---|
| Sign-in (any account, guest) | VERIFIED WORKING (live) |
| Upload a PDF bill | VERIFIED WORKING on staging 19 Oct 2026 (fictional PDF saved and read, survives reload, not-a-PDF and over-10-MB rejected with a message, labelled "Uploaded", guest sees nothing); second signed-in account not yet tested |
| Gmail connect + sync | PARTIALLY: production has the Google client and push configured (19 Oct 2026 health: 2 active watches, notices accepted); Google test users only until Google verification (restricted scope, CASA). Staging has no Google secrets, so it can't test Gmail (steps in DEPLOYMENT.md) |
| Two ways in (Explore the demo / Use my own bills) | PARTIALLY VERIFIED on staging 19 Oct 2026: demo screens show a fictional-data strip with "Go to my Home" (checked signed in). The guest welcome card can't be checked on a deployed app (the browse tool refuses guests) |
| Add a bill by photo or pasted text | VERIFIED WORKING on staging 19 Oct 2026 for pasted text (fictional Lumo Vesi bill read, reviewed, deduplicated); photos (JPG/PNG) by unit tests only: OCR runs only with AI reading on |
| AI explanation of uploaded or pasted bills | UNIT TESTED: "Explain with AI" works without Gmail, from the saved record; opt-in only. Demo explanations stay rule-based (no AI for guests, to keep costs and abuse in check) |
| Forwarding address | BLOCKED: IKON inbound email returns 403 |
| Classify + extract + explain (rules, PDF) | PARTIALLY: tested on fictional data |
| Document routing: Bills, Tax, Debt, Income, Spending, Needs review | VERIFIED WORKING (unit tests for each kind; inbox views on staging c0a16ace) |
| Personal inbox: review, correct, paid, delete, export | VERIFIED WORKING (local run, fictional data) |
| Payslips separate from bills | PARTIALLY |
| Payday plan from real records | PARTIALLY |
| Month-by-month summary | PARTIALLY |
| Debts, Savings goals | VERIFIED WORKING on staging 646efeaa: "Debts & goals" with per-user om_debts/om_goals/om_contributions, add/edit/delete, money in/out, payoff and pace estimates, survives reload; isolation by unit tests (a deployed guest check is refused by the browse tool). Demo screens stay fictional |
| Home / Attention Center | VERIFIED WORKING on staging a37ce4de: "My money" is now "Home" with a Reminders list first (overdue, due today, due in 7 days, debt payments, items to check, payday), each opening its record; unit tests in AttentionTests |
| Reminders | VERIFIED WORKING in-app only (staging a37ce4de): built from saved records, count on the Home menu item; nothing is sent. Push or email reminders are not built |
| Tax & Vero (tax card, income limit) | MISSING |
| Ask about my records (AI with sources) | MISSING |
| Email drafts (copy only, no sending) | VERIFIED WORKING on staging 19 Oct 2026: "Ask for more time" on an open bill drafts an English or Finnish email from the saved details only, lists what's missing, Copy draft; never sent |
| OCR for scans | MISSING |
| Privacy policy | MISSING (needs the owner's details) |
| Deploying | VERIFIED WORKING: staging a37ce4de (steps 1-4); production c62e28b1 = commit 2ceea6f (steps 1-3), deployed 19 Oct 2026 with the owner's approval; Debts & goals screen checked signed in |

## V1 steps, in order (each ends with a check)

0. **Unblock deploy.** Hand-fix `frontend-node` boot files; build; staging deploy without activation.
   Done when staging runs the current code.
1. **Upload live.** On staging: fictional PDF saved, bad/oversized file rejected, survives refresh,
   second user can't see it.
2. **Document routing.** Add Tax and Debt categories and a Needs Review state to the classifier.
   Done when tests route invoice, payslip, tax and debt letters correctly and unclear ones go to review.
3. **Real Debts and Goals.** Per-user `om_debts`, `om_goals`, `om_contributions`; reuse the screens;
   demo stays separate. Done when CRUD, ownership and reload tests pass.
4. **Home / Attention Center + in-app reminders.** Overdue, due in 7 days, needs review, next payday,
   only from saved records.
5. **Finland + AI.** Tax card values (user-entered) with income-limit tracking; "Ask about my records"
   answering from the user's own records with sources; drafts to copy, never sent.
6. **Email (parallel, external).** Google app verification for Gmail; IKON inbound email for forwarding.
   Stays BLOCKED with the exact reason until the provider allows it.
7. **Pilot readiness.** Privacy policy, rate and cost limits, backup/restore check, staging test pass.
   Then ask before production.

Out of V1: bank connections, payments, MyTax/Kela access, other countries, business workspace.
