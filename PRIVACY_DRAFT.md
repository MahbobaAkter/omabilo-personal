# Omabilo privacy notice: DRAFT for review

> **Not published.** This draft describes what the code does as of 17 Oct 2026. Before using it:
> - Fill in the bracketed parts.
> - Have it checked against the GDPR and Google's API Services User Data Policy.
> - Save it as `legal/privacy.md`, which the app then serves at `/legal/privacy`.

**Who we are:** [Company name, business ID, address]. Contact: [privacy email].

## What Omabilo does with your Gmail

When you choose **Connect Gmail**, Google asks you to allow Omabilo **read-only** access
(`gmail.readonly`). Omabilo cannot send, delete or change your email. It uses this access only to find
financial messages: bills, payment reminders, payslips, receipts, refunds and tax notices. It then
organises and explains them for you.

**What Omabilo reads:**
- For each recent message, the sender, subject, date and Google's short preview.
- The full message, and PDF or text attachments up to 10 MB, only when the preview suggests it may be financial.

**What Omabilo keeps:**
- **Financial messages:** sender, subject, date, a short excerpt of at most 800 characters (card numbers and identity codes hidden), the details read from it, and your corrections.
- **Other messages:** only Gmail's message id, so they aren't read again. These are deleted after 180 days.
- **Google access tokens:** encrypted, on the server only. They're removed when you disconnect, and Omabilo asks Google to revoke its access.
- **An activity log:** what happened, never amounts or email text. Deleted after one year.

## AI (optional, off by default)

If you turn on **Explain my financial emails with AI**, Omabilo sends a shortened copy of each
financial message to an AI model (Claude Haiku, through the IKON platform, with processing pinned to
the EU) to write a plain-English explanation. Account numbers, identity codes, card numbers, email
addresses and phone numbers are removed first. The AI never decides amounts, dates or statuses.
[Confirm and name the processor terms that apply.]

## Google API Services User Data Policy

Omabilo's use of information received from Google APIs adheres to the
[Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy),
including the Limited Use requirements:
- Gmail data is used only to provide the features you see.
- It is not used for advertising, and it is not sold.
- People don't read it, unless you ask for support, security requires it, or the law requires it.

## Your controls

- Disconnect Gmail at any time under **Connections & privacy**.
- Correct, mark or delete any record.
- **Export** everything Omabilo keeps about you as a JSON file.
- **Delete all my Omabilo data** removes your records, settings and activity log at once.
- **Delete my account** removes your account and data after a grace period you can cancel.

## Where data is stored

Data is stored in IKON's managed database in the EU. [Confirm backups, encryption at rest and
sub-processors with IKON before stating them.]

## Legal basis and contact

[Legal basis (e.g. performance of a contract / consent for AI processing), retention, your rights
under the GDPR, and how to complain to the Data Protection Ombudsman (tietosuoja.fi).]
