# OMABILO — MASTER PRODUCT CHECKLIST
**Purpose:** Ship a functional, reliable, polished Omabilo V1 without duplicate work.

## RULES FOR EVERY IMPLEMENTATION
- Read existing `AGENTS.md`, `CLAUDE.md`, `PROJECT_STATUS.md`, `DECISIONS.md` and this checklist.
- Treat existing code, tests and project decisions as the source of truth. Reuse working components and business logic.
- Do not rebuild working features, repeat completed audits, create duplicate screens or invent functionality.
- Preserve unrelated and uncommitted changes. Never overwrite user work.
- Implement related changes together. Run relevant tests/build once after the batch.
- Prioritize working functionality over visual polish. Report actual results and blockers concisely.
- Never claim a feature works unless implemented and verified.
- Never commit, push, run `ikon save` or deploy without explicit approval. Verify the deployment target before any approved deployment.

## P0 — FUNCTIONALITY AND CORRECTNESS

### 1. Financial document processing
- Collect documents from supported sources and route them into one personal financial record system.
- Correctly classify: bills/invoices, salary/payslips, confirmed income, expected income, receipts, refunds, tax documents, debt/collection letters, spending and needs-review items.
- Send uncertain classifications to review; let users correct categories and extracted fields.
- Detect duplicates and reminders. Do not double-count records or automatically merge uncertain matches.
- Keep original source records linked to canonical records and preserve user corrections.
- Extract issuer/company, amount, currency, invoice number/reference, invoice date, due date and payment status when available.
- Remove heading contamination such as “LASKU” from company names.
- Clearly distinguish extracted, estimated, user-confirmed and verified information.

### 2. Salary and income — never confuse expected with received
- An accountant/bookkeeper's payroll document or payslip represents **expected salary**, not money already received.
- Display expected salary separately from confirmed salary received.
- Count salary as credited money only after the user confirms receipt or a reliable matching payment confirmation is processed.
- Support other income and refunds without misclassifying them as loans or debts.
- Keep payable invoices, incoming income, outgoing expenses, loans and debt repayments distinct.
- Prevent the same document or payment from appearing twice in totals.
- Allow the user to correct a mistaken classification.

### 3. Inbox, Invoices and Income
- Remove overlapping functionality without removing useful capabilities.
- **Financial Inbox:** central place to review incoming financial documents, their source, category, confidence and required action.
- **Invoices/Bills:** canonical payable records, with amount, issuer, due date, status, explanation and payment/review actions prominent.
- **Income:** expected versus received salary, other credits and their allocation.
- Link related records across these views; do not create separate copies of the same financial event.
- Remove the Home paste-text button, but preserve the existing supported paste-text backend/workflow where still accessible.
- Keep PDF upload and existing supported intake methods functional.

### 4. Email accounts and synchronization
- Support collecting financial information from multiple email accounts into the same user's categorized financial inbox, without mixing different users' data.
- Make each account's connection, sync progress, last successful sync, errors and reconnect requirements clear.
- Handle expired trials accurately; stop restricted sync when required and never display a false “connected and syncing” state.
- Prevent overlapping syncs, repeated ingestion and cross-account duplication.
- Keep account disconnection and access revocation effective.
- Extend the existing data model safely; preserve current accounts, tokens, records and user corrections during migration.
- Verify which email providers and account combinations are actually supported before displaying them as available.
- Gmail forwarding is externally blocked/unverified in the known project state. Never claim it works until verified end-to-end.

### 5. Financial calculations
- Use existing shared calculation logic rather than duplicating formulas across screens.
- Payday Planner: clearly label confirmed, scheduled, estimated and planned amounts. Never count unconfirmed salary as available money.
- Debts: show balances, payments, interest and payoff estimates accurately.
- Goals: show progress, contributions and remaining amounts accurately.
- Monthly Reports: separate actual from expected income and spending; never count unpaid bills as paid.
- VAT/tax explanations: use clear dates and distinguish an invoice date from its due date.
- Accountant email drafts: use the same reporting month in the explanation and draft; handle an empty document list honestly.
- Keep calculations consistent across Home, Income, Bills, Payday, Debts, Goals and Reports.

### 6. Existing bug fixes
- Fix accountant email reporting-month mismatch and misleading empty-document drafts.
- Fix invoice issuer names polluted by headings such as “LASKU”.
- Clarify VAT due-date wording.
- Fix Gmail trial-expiry, syncing, stale status and reconnect inconsistencies.
- Add regression tests for these fixes.

## P1 — HOME AND FINANCIAL SCOREBOARD

### 7. Home dashboard
- Provide a quick, trustworthy view of the user's financial situation.
- Include available money, expected income, upcoming bills, debt and savings/goals.
- Add a simple money-flow visualization and one useful next action.
- Use real saved records and existing calculation logic; do not invent a financial score or display fake data.
- Keep the layout spacious, readable, mobile-friendly and easy to understand at a glance.
- Avoid duplicating the full Inbox, Bills, Income or Reports screens on Home.

### 8. Nordic visual identity
- Palette: warm ivory, pine/forest green, teal, glacier blue and muted amber. No pink.
- Scandinavian winter atmosphere, subtle snowflake details, warm and optimistic rather than cold.
- Clean typography, consistent spacing, restrained animations and polished shared components.
- Use the images in `reference/design/` as visual guidance, not as instructions to copy every element.
- Keep the existing `home-cover.svg` only if it supports the finished design; do not let the cover dominate the dashboard.

### 9. Personalization
- Optional profile photo and cover image uploads.
- Curated Omabilo avatars and cover images.
- AI-generated avatars, including from a user's own photo, only when a real, supported generation flow exists with consent, privacy protection and clear cost controls.
- Offer a small set of coordinated themes: Sage, Ocean, Forest and Warm Neutral.
- Maintain accessible contrast and consistent meanings for financial status colors.
- Include preview and reset-to-default controls. Do not fake uploads or AI generation.

## P2 — TRUST, ACCESS AND RELEASE READINESS

### 10. Navigation and access
- Keep Debts and Goals separately accessible.
- Preserve the assistant's free access and existing intended Premium restrictions.
- Make plan restrictions and trial status truthful and understandable.
- Do not remove existing working sign-in, account management, export, deletion or privacy controls.

### 11. Privacy and reliability
- Enforce per-user data isolation for every account and record.
- Protect email tokens and sensitive financial information.
- Do not put message contents, financial amounts, tokens or document text in logs/audit events.
- Preserve consent and access revocation.
- Keep the privacy policy, retention, deletion and backup/restore requirements visible as release blockers where unfinished.
- Apply AI only through the existing approved architecture; validate output and control cost. Never let AI invent financial amounts.

### 12. Implementation and release
- First finish the current Nordic Home implementation.
- Next, implement P0 functionality and correctness fixes in one focused batch where safe.
- Add or update tests for changed behavior; run the relevant build/tests once.
- Implement multi-account support without losing existing data or breaking current sync.
- Then finish the Home scoreboard, personalization and remaining visual polish.
- Deploy only after explicit approval and confirmation of the correct production target.
- Test the live app using a dedicated test account and fictional/test financial data; verify key user flows and data isolation.
- Report only: changes completed, tests/build results, deployment status and genuine remaining blockers.

## DEFINITION OF DONE
A feature is complete only when:
1. It works in the existing app and uses real saved state where required.
2. It does not duplicate financial records or mix users' data.
3. Errors, uncertainty and unsupported functionality are shown honestly.
4. Relevant regression tests pass.
5. Existing working features and unrelated changes remain intact.
