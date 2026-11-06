# Acceptance tests (Playwright)

End-to-end tests that drive the real web app in a browser against the development API host and PostgreSQL. They cover journeys a user takes through the UI, in Arabic and English, with axe accessibility checks. Rules the API enforces on its own (RLS, cross-tenant sweeps, permission evaluation) stay in `tests/Integration` and `tests/Security`. The coverage plan for R1 is in [`docs/testing/e2e-plan.md`](../../docs/testing/e2e-plan.md).

## Run locally
Requirements: the quick start in the repository README (PostgreSQL with the TeamBeats roles, .NET 10 SDK, Node 22).

```bash
cd src/Web && npm ci && cd -
cd tests/Acceptance
npm ci
npx playwright install chromium   # once per Playwright version
npm test                          # starts the API host and the web app, or reuses ones already running
npm run test:ui                   # interactive mode
npm run report                    # last HTML report
```

Playwright starts `src/Api.DevHost` on `http://localhost:5080` and `next dev` on port 3000 with development sign-in. If you already run them yourself (as in the README quick start), set `Http__TrustForwardedHeaders=true` on the API host, because the tests call the API on loopback with `x-forwarded-host`, the same way the web app does.

## Settings
| Variable | Default | Use |
|---|---|---|
| `E2E_TARGET` | `local` | `remote` runs against a deployed environment and starts no servers |
| `E2E_BASE_DOMAIN` | `teambeats.localhost` | Tenant hosts are `<key>.<base domain>` |
| `E2E_WEB_PORT` | `3000` locally, none remotely | Port of the web app |
| `E2E_SCHEME` | `http` locally, `https` remotely | Scheme of tenant hosts |
| `E2E_API_ORIGIN` | `http://localhost:5080` | API origin for test data setup |
| `E2E_PLATFORM_OPERATOR` | `operator@platform.example` | Seeded fictional platform operator |

## Writing tests
- Create data through the public API in `support/platform.ts`. Every test makes its own tenant with a unique key, so tests run in parallel and never depend on each other.
- Take expected text from the web catalogues with `msg(locale, key)`. Never hard-code user-facing strings.
- Find elements by role and accessible name (`getByRole`, `getByLabel`). If a control cannot be found that way, that is an accessibility bug to fix in the app, not a reason to add a test id.
- Call `expectAccessible(page, testInfo, label)` on every new screen. The gate is zero critical axe violations (spec 007 US5); other findings are attached to the report.
- Run a user-facing journey in both `ar` and `en` with `test.use({ locale })`.
- Use fictional data only (`*.example` addresses). No real people, customers, tokens or secrets.
- No fixed waits. Wait on URLs, roles and assertions.
