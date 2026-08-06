# Sample — the CRM's web client

**Claim proved:** the design handed over as an HTML prototype is implemented as a React
application whose data layer is the CRM's own API — twenty-two of its screens read and write the
real backend, and the rest render from the object model rather than from screen-specific mock-ups.

```bash
# The backend, on the port the dev server proxies to.
FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=postgres;Username=postgres" \
  dotnet run --project samples/crm

# The client.
cd samples/crm-web && npm install && npm run dev     # http://localhost:5173
```

`vite.config.ts` proxies `/api` to `http://localhost:5000` (override with `CRM_API`), so the
browser treats the API as same-origin. That is deliberate: the CORS policy in `Program.cs` is what
a deployed client needs, and a dev server that depended on it would hide a broken one.

## How it is put together

| Layer | Where | What it may know |
|---|---|---|
| Tokens | `src/design/tokens.css` | Colour, type, space, geometry. Nothing else names a colour. |
| Primitives | `src/design/primitives` | How things look and behave. **Nothing about a CRM.** |
| Charts | `src/design/charts` | Bars, waterfall, funnel, sparkline — layout, not a charting library. |
| Shell | `src/shell` | The chrome and the navigation table. |
| API | `src/api` | Routes, contracts, one hook per backend surface. |
| Features | `src/features/*` | Composition. A screen is primitives plus one hook. |

Three rules hold the layering up, and each is worth stating because breaking it is cheap and the
cost arrives later:

- **A primitive never imports a feature, a contract or a query.** That is what lets the button in
  the executive board be the same button as the one in setup.
- **A screen never calls `fetch`.** Every read and write goes through `src/api/queries/hooks.ts`,
  which is the only file that knows a route, a token, an idempotency rule and a cache key. The
  fourth screen is where writing that four times starts costing.
- **Every cache key lives in `src/api/queries/keys.ts`.** A key spelled in two files is two caches,
  and the symptom is a mutation that refreshes the list but not the tile above it.

## What is wired to the backend

| Screen | Endpoint |
|---|---|
| Service console, case board | `POST /service/queue`, `/service/comments` |
| Business hours and SLA | `POST /service/hours`, `/service/policies` |
| Work inbox | `POST /approvals/inbox`, `/approvals/decisions`, `/service/queue` |
| Approvals setup | `POST /approvals/processes` |
| Quote builder — submit | `POST /approvals/requests` |
| Campaign performance, attribution | `POST /campaigns/performance`, `/campaigns/attribution` |
| Executive board, exec home | `POST /board` |
| Scorecard, reviews | `POST /kpis/scorecards` |
| Sales and deal performance | `POST /performance/sales`, `/performance/deals`, `/quotas/attainment` |
| Portfolio, strategy | `POST /planning/roll-ups`, `/planning/tree` |
| Search | `POST /search` |

The remaining screens — the record surfaces, the setup editors, the planning detail — read
`src/fixtures/objects.ts`, which carries the prototype's own object model and records. **The
backend has no list or record endpoint for the built-in entities**, so those screens read the
model rather than inventing an API the server does not serve. Each hook they would use has the
same shape as a query hook, so wiring one up later is a change to one import.

## What is worth reading

- `src/design/primitives/DataTable.tsx` — sorting is opt-in per column, because a header offering
  to sort a column the server ordered would silently reorder a page rather than a result.
- `src/api/client.ts` — every refusal is a problem document with a code and a sentence written for
  a person. Throwing that away and rendering "something went wrong" discards the only useful part.
- `src/features/analytics/CampaignScreen.tsx` — the attribution model is named beside every number
  it produced, and the gap between what was considered and what was attributed is shown rather
  than closed.
- `src/features/sales/KanbanScreen.tsx` — cards move by drag *and* by arrow key, and the move says
  it is not written: the backend advances an opportunity by announcing an intent, not by being
  told which column to put it in.

## Checks

```bash
npm run typecheck    # strict, with noUncheckedIndexedAccess and exactOptionalPropertyTypes
npm test             # 34 tests
npm run build
```

The tests cover the two places a defect would be invisible: the formatters, where a null that
renders as `0%` puts a campaign that reached nobody below one that converted one in a thousand;
and the primitives' behaviour, where a card that navigates without being a button, a tab strip
that only answers a mouse, and a hint that is rendered but not announced all look correct on
screen. Each was proved by breaking it and watching the right test fail.
