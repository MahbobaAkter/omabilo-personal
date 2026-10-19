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
| Upload a PDF bill | VERIFIED WORKING on staging 19 Oct 2026 (fictional PDF saved and read, survives reload, not-a-PDF rejected, guest sees nothing); 2 small fixes open |
| Gmail connect + sync | PARTIALLY: live OAuth works for Google test users only; no real bill read yet |
| Forwarding address | BLOCKED: IKON inbound email returns 403 |
| Classify + extract + explain (rules, PDF) | PARTIALLY: tested on fictional data |
| Personal inbox: review, correct, paid, delete, export | VERIFIED WORKING (local run, fictional data) |
| Payslips separate from bills | PARTIALLY |
| Payday plan from real records | PARTIALLY |
| Month-by-month summary | PARTIALLY |
| Debts, Savings goals | UI/DEMO ONLY (demo data, not per-user records) |
| Home / Attention Center | MISSING |
| Reminders | MISSING |
| Tax & Vero (tax card, income limit) | MISSING |
| Ask about my records (AI with sources) | MISSING |
| Email drafts (copy only, no sending) | MISSING |
| OCR for scans | MISSING |
| Privacy policy | MISSING (needs the owner's details) |
| Deploying | VERIFIED WORKING on staging (ab781cfa, frontend bridged to SDK 1.13.9); production not redeployed |

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
