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
