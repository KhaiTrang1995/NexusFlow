# CRM web client — every screen, captured

46 routes, taken twice: once as **rep** and once as **admin**. Both sets run against the real
backend (`samples/crm`) on a seeded `northwind` tenant, not against fixtures.

Captured 2026-08-10 with `samples/crm-web/shots.mjs` at 1440×900, full page.
`report.json` carries the per-screen result.

## Why two personas

The client's persona switcher maps to the backend's three authorisation stances, so the same
screen is a different screen depending on who is looking at it. Six routes read
`POST /api/v1/crm/config`, which requires `crm.admin`:

`/service/sla`, `/analytics/reports`, `/setup/list-views`, `/setup/approvals`,
`/setup/validation`, `/setup/onboarding`

Under `rep/` those six show the refusal the server sent — *"Capability 'crm.config.list'
requires the 'crm.admin' permission and the caller does not hold it"* — rather than an error or
an empty page. That is the behaviour worth having a picture of. Under `admin/` the same six
render their content.

Every other route renders identically for both.

## Reproducing

```bash
FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=postgres;Username=postgres" \
CRM_SEED_FILE="$PWD/samples/crm/seed/northwind.json" ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project samples/crm

cd samples/crm-web && npm run dev
node shots.mjs
```
