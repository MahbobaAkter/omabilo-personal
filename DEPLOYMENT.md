# Deploying Omabilo Personal (website + API)

Nothing here has been deployed. This guide says what each piece needs and what still blocks a
public link.

## How the pieces fit

```
iPhone / laptop ──https──▶ website (web/, on Vercel) ──https──▶ API (backend/, in a container) ──▶ database
```

- `web/` is a static React site. Vercel can host it as it is.
- `backend/` is the ASP.NET Core API (.NET 10). It needs a host that runs containers or .NET 10, and a real database.
- `app/` and `frontend-node/` are the separate IKON app. They are not part of this deployment.

## What blocks a public link today

1. **Sign-in.** Outside Development the API refuses to start without a real identity provider. The fictional demo users (`X-Demo-User`) work only on a Development server, never on a public one. **Needs your decision:** which provider to use. Free tiers exist (for example Microsoft Entra External ID or Auth0), but each needs an account you create.
2. **Database.** The only stores today are in-memory (lost on restart) and a local SQLite file (one machine; most container hosts wipe it on redeploy). A hosted Postgres store has to be written behind `IInvoiceStore` and `IFinanceStore` first. **Needs your decision:** which Postgres host.
3. **Website sign-in.** `web/` supports only demo sign-in or none. With a real provider it also needs a sign-in flow that gets an access token and sends it as `Authorization: Bearer …` (today it sends only `X-Demo-User`).
4. **Docker image not built yet.** Docker Desktop is installed but hasn't finished its first-time setup on this PC (no Docker Linux environment exists under WSL), so `docker build` couldn't run. Instead, the Dockerfile's exact file list was copied to an empty folder and `dotnet publish -c Release` run on it. It built, and the published app passed the Production-mode checks (17 Oct 2026).

## Website on Vercel

| Setting | Value |
|---|---|
| Root Directory | `web` (change it from `frontend-node`) |
| Framework preset | Vite |
| Build command | `npm run build` |
| Output directory | `dist` |
| Environment variable | `VITE_API_BASE_URL` = the API's `https://` address, e.g. `https://omabilo-api.example.com` |
| Environment variable | `VITE_AUTH_MODE` = `none` until a sign-in provider is connected |

- `VITE_` values are built into the site and **visible to every visitor**. Never put secrets in them.
- The site refuses a `localhost` or `http://` API address in a production build, and says so on screen.
- `web/vercel.json` sends every path to `index.html`, so links such as `/payday` work.

## API container

Build from the repository root (the API uses shared code from `app/Omabilo`):

```bash
docker build -f backend/Dockerfile -t omabilo-api .
```

The container listens on port **8080**. Configure it with environment variables only:

| Variable | Meaning |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `Auth__Authority` | Your identity provider's issuer URL |
| `Auth__Audience` | The API's audience/identifier at that provider |
| `Cors__AllowedOrigins__0` | The website's exact address, e.g. `https://omabilo.vercel.app` (no trailing slash) |
| `Storage__Provider` | `InMemory` for a throwaway test; a durable Postgres provider once written |
| `ASPNETCORE_HTTPS_PORT` | `443`, the public HTTPS port the API redirects plain-HTTP visitors to |
| `Proxy__TrustAllProxies` **or** `Proxy__KnownNetworks__0` | Which proxy's forwarded headers to believe. See below. |

Notes:
- Keep the identity provider's secrets and the database connection string in the host's **secret manager**, never in git.
- Set the host's health check to `GET /health`. It needs no sign-in, returns `{"status":"ok"}`, and is never redirected, so a plain-HTTP health check works.
- The API allows browser calls only from the exact origins listed in `Cors__AllowedOrigins__*`.

### HTTPS behind the host's proxy

Hosting services end HTTPS at their own proxy and pass the request to the container as plain HTTP.
They add an `X-Forwarded-Proto: https` header to say the visitor really used HTTPS.

The API believes that header **only from a trusted proxy**. Otherwise anyone could fake it.
- **Nothing outside the container is trusted by default.** In that state, every request looks like plain HTTP and is redirected to HTTPS. Browser preflights then fail, and the website can't reach the API.
- **Choose exactly one setting.** Use `Proxy__TrustAllProxies=true` **only** if the container can be reached solely through the host's proxy, with no public port of its own; most platform proxies work this way. Otherwise list the proxy's private network, for example `Proxy__KnownNetworks__0=10.0.0.0/8`, taking the range from the host's documentation.
- **Once set:** requests through the proxy count as HTTPS, so there's no redirect, browser preflights work, and responses carry HSTS. Plain HTTP not from the proxy is still redirected, and sign-in and CORS are unchanged.

**Tested locally:** a simulated proxy (`backend/tests/.../ProxyTests.cs`). **Not verifiable locally:**
- which headers a given host really sends
- whether its container port is private
- its proxy's address range

Check these in the host's documentation before choosing the setting.

## Before real people or real invoices

These are not done yet. Don't call the service secure until they are:
- a real identity provider
- a hosted Postgres store with per-user rows
- encryption at rest confirmed with the host
- private document storage
- tested backup and restore
- an account deletion flow
- a retention policy

Invoice export and delete-all already exist in the API.

## Connected companion on IKON (Gmail)

The connected companion (sign-in, Gmail, sync, AI and the "Your money" screens) runs in the IKON
app itself, so it needs no Vercel or container settings. It needs **IKON secrets**, set from the app
folder. Values are prompted for and never shown; don't put them in files, chat or Git.

| Secret | What it is | Set? |
|---|---|---|
| `OMABILO_TOKEN_KEY` | Base64 of 32 random bytes; encrypts Google tokens (AES-256-GCM) | Set 17 Oct 2026 |
| `GOOGLE_OAUTH_CLIENT_ID` | OAuth client id from Google Cloud | **You** |
| `GOOGLE_OAUTH_CLIENT_SECRET` | OAuth client secret from Google Cloud | **You** |

Rotating `OMABILO_TOKEN_KEY` makes stored Google tokens unreadable. Everyone then has to reconnect
Gmail, and nothing else is lost.

### Google Cloud steps (you do these; about 20 minutes)

1. **Create the project.** Go to <https://console.cloud.google.com/>, then **Select a project → New project**. Name it `Omabilo`.
2. **Enable the Gmail API.** Go to **APIs & Services → Library**, search for "Gmail API" and click **Enable**.
3. **Set up the consent screen** under **APIs & Services → OAuth consent screen** (in newer consoles: **Google Auth Platform → Branding / Audience / Data access**):
   - User type: **External**. Publishing status: leave it on **Testing**.
   - App name **Omabilo**, your support email, and these links:
     - home page `https://omabilo-2143f1e7.ikonai.app`
     - privacy policy `https://omabilo-2143f1e7.ikonai.app/legal/privacy`
   - Under **Data access → Add or remove scopes**, add only `https://www.googleapis.com/auth/gmail.readonly`.
   - Under **Audience → Test users**, add the Gmail addresses of your test accounts. Use a dedicated test Gmail, not your personal inbox. Testing mode allows up to 100 test users.
4. **Create the OAuth client.** Go to **APIs & Services → Credentials → Create credentials → OAuth client ID**:
   - Application type: **Web application**. Name: `Omabilo IKON`.
   - **Authorized redirect URIs**: `https://omabilo-2143f1e7.ikonai.app/api/oauth/google/callback`. To test a local `ikon run` as well, also add `https://localhost:9443/api/oauth/google/callback`.
   - Click **Create**. Copy the client id and secret straight into the commands below, not into chat or a file.
5. **Store them in IKON.** From `<your-local-project-path>`, run these in a terminal. Each one prompts for the value and masks it:

   ```bash
   ikon secret set GOOGLE_OAUTH_CLIENT_ID
   ```

   ```bash
   ikon secret set GOOGLE_OAUTH_CLIENT_SECRET
   ```

   Before creating the client, confirm the redirect URI: once the current code is deployed,
   <https://omabilo-2143f1e7.ikonai.app/api/health> shows the exact `googleRedirectUri` this server sends
   to Google. Register that exact value. It shows no secret values, only whether each is set.
6. **Restart the app so it reads the secrets.** A deployed app reads them when an instance starts: deploy again, or wait for the next cold start. For a local run, use `ikon stop`, then `ikon run --detach`.
7. **Verify the connection:**
   1. Sign in to the live app with a test-user Google account. Go to **Connections & privacy → Connect Gmail**.
   2. Google's screen should name Omabilo and ask only to "Read your email messages and settings".
   3. After you allow it, you return to a "Gmail connected" page. "My money" then shows "Looking for financial messages…", followed by results within a few minutes.
   4. Check the logs with `ikon logs --level info`. Expect `scheduled gmail sync` every 15 minutes, and never any message text.

### Sign-in and Gmail are separate

- Signing in to Omabilo (Google, email code, passkey…) is IKON's own sign-in. It never grants
  Gmail access.
- Gmail is a second, explicit consent: **Connections & privacy → Connect Gmail**. It uses its own
  OAuth client (above), scope `gmail.readonly` only, PKCE and a one-time state.
- Tokens are encrypted on the server with `OMABILO_TOKEN_KEY`. They never reach the browser.
- **Disconnect Gmail** revokes Google's grant and stops sync. People can also check or remove
  access at <https://myaccount.google.com/permissions>.

### Gmail on staging

On 19 Oct 2026, staging's `/api/health` reports `googleOAuthConfigured: false` and
`tokenKeyConfigured: false`, so Connect Gmail can't work there. Production has both. To test Gmail
on staging:

1. In the same Google OAuth client, add the redirect URI
   `https://omabilo-staging-e1e3d04b.ikonai.app/api/oauth/google/callback`.
2. From the app folder, set the three secrets for staging. Each command prompts for its value.

   ```bash
   ikon secret set OMABILO_TOKEN_KEY --target staging
   ```

   ```bash
   ikon secret set GOOGLE_OAUTH_CLIENT_ID --target staging
   ```

   ```bash
   ikon secret set GOOGLE_OAUTH_CLIENT_SECRET --target staging
   ```

   Use a new random key for staging, not production's.
3. Redeploy staging. Check that `/api/health` shows both settings as configured.

### Other ways bills arrive (no mailbox needed)

**My inbox → Add a bill** takes three kinds of input:

- **A PDF.**
- **A JPG or PNG photo.** Photos are read with the platform's EU OCR, only on Premium and only when
  the person has turned on AI reading.
- **Pasted text.**

All three go through the same reading, review and duplicate rules as Gmail. Gmail is never
required.

### Free and Premium (€2.99 a month)

There is no demo any more. Before sign-in people see a welcome page with the two plans; after
sign-in every screen shows their own records.

- **Free:** add bills as a PDF or pasted text, rule-based explanations, review, reminders on Home,
  "Ask for more time" drafts, and Gmail for the first 30 days after the first connection.
- **Premium:** Gmail after the 30 days, the forwarding address, photo and scan reading (OCR), AI explanations, Debts &
  goals, the payday plan and the month-by-month summary. The list lives in
  `app/Omabilo/Companion/Membership.cs`.

Until the steps below are done, "Get Premium" is disabled with the reason shown. Run them yourself
from the app folder; each one asks before it changes anything:

1. **Enable Mollie:** `ikon payments enable mollie`. It opens Mollie's onboarding, where you enter
   the business, bank and identity details. Check progress with `ikon payments status` until
   charges are enabled. IKON takes a platform fee on each payment (see `ikon payments` docs).
2. **Create the offer:**
   `ikon offer create premium --name "Omabilo Premium" --amount 299 --currency eur --interval month`.
   The id must be `premium`.
3. **Optional, complimentary Premium for your test accounts:** `ikon secret set OMABILO_PREMIUM_EMAILS`
   with a comma-separated list of sign-in emails.
4. Redeploy (or wait for a cold start) so the app reads the secret.

People pay on Mollie's own page; Omabilo never sees card or bank details. They cancel in
**Plan & privacy** and keep Premium until the paid month ends.

**Gmail trial:** it starts at a person's first Gmail connection (`GmailTrialStartUtc` in their
settings). Reconnecting doesn't restart it; connections made before the trial existed count from their
connection day. After 30 days the server stops reading that mailbox (scheduled sync, push and Sync now)
unless the person has Premium, and checks again hourly. Records already found stay. **Known gap:**
"Delete all my Omabilo data" also deletes the settings, so someone who deletes everything gets a new
trial.

### Before people outside your test list

- **Google verification:** `gmail.readonly` is a *restricted* scope. Leaving Testing mode needs Google's app verification and an annual third-party security assessment (CASA). Plan weeks for this, with cost depending on the assessor.
- **Testing mode tokens expire:** refresh tokens expire after **7 days** in Testing mode. Testers then see "Reconnect Gmail", which is expected.
- **Privacy policy:** the page at `/legal/privacy` must describe Gmail use, including Google's "Limited Use" requirements. A factual draft is in `PRIVACY_DRAFT.md`; review it and save it as `legal/privacy.md`.
- **AI processing:** before turning AI on with real email, confirm IKON's data-processing terms for AI calls. The app pins calls to the EU, removes account numbers and identifiers, and asks each person for opt-in.
