# Omabilo Personal website (`web/`)

React + Vite + TypeScript. It talks only to the standalone API in `backend/`; it has nothing to do
with IKON (that's `frontend-node/`).

## Run locally

```bash
cd backend
dotnet run --project src/Omabilo.Api
```

```bash
cd web
npm install
npm run dev
```

The API starts on http://localhost:5080 in Development mode with fictional demo users. The website
starts on http://localhost:5173. Before the first run, copy `.env.example` to `.env.local`; it
points the site at the local API.

## Check it

```bash
npm test
npm run build
```

`npm test` runs the unit tests. `npm run build` type-checks and builds into `dist/`.

## How it behaves

- **The API address** comes from `VITE_API_BASE_URL`. The site shows a clear message, not fake data, when it's missing, isn't reachable, or is `localhost` on a published site.
- **The dot in the header** shows the server connection. It is checked every 30 seconds and when the device goes on or offline.
- **Changes** show a success message only after the server confirms them.
- **Demo data** is labelled "Fictional demo data". Simulated invoice imports are labelled "Simulated".

Deployment: see `../DEPLOYMENT.md`.
