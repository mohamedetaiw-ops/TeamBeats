# TeamBeats

Spec-driven development repository for TeamBeats (working name), an AI-native enterprise intranet. Built from `AI_Intranet_Product_Foundation` v0.1.

The layout follows GitHub spec-kit conventions so `/specify`, `/clarify`, `/plan` and `/tasks` style workflows work.

| Path | What it is |
|---|---|
| `docs/review/foundation-v0.1-review.md` | Critical review of the foundation, with the decisions needed |
| `docs/review/architecture-patterns-review.md` | Architecture patterns recommendation reasoned against the constitution |
| `.specify/memory/constitution.md` | The constitution (v1.1.0, ratified) |
| `.specify/templates/` | Spec, plan, tasks and ADR templates |
| `specs/` | Feature specs: 001 to 007 (Approved, with plans and tasks) and 008, 009, 011, 013, 016, 020, 021, 022, 024 to 027, 032, 033 (Proposed). Index, R0 build order and planned specs in `specs/README.md` |
| `docs/architecture/r0-solution-structure.md` | Shared R0 repo layout, building blocks, test harness and CI gates |
| `docs/architecture/module-map.md` | Backend modules as bounded contexts: owned data, contracts, events |
| `docs/architecture/decisions/` | ADR-0001 to ADR-0014. ADR-0005, ADR-0013 and ADR-0014 are Accepted; the rest are Proposed |
| `docs/product/roadmap.md` | Release slices R0 to R3 (adopted) |
| `docs/product/competitor-feature-coverage.md` | Where each competitor capability lives in the specs |
| `docs/product/composable-experience-requirements.md` | Composable Intranet Experience requirements (mandatory, 182 IDs cited as `CX-`) |
| `docs/product/composable-experience-coverage.md` | Spec and FR for each composable experience requirement |
| `specs/open-questions.md` | Remaining spec questions with defaults |
| `docs/product/foundation-v0.1.md` | The source foundation document, with its diagrams in `assets/` |
| `CLAUDE.md` | Rules for AI coding agents working in the repo |

## Code layout (R0)
| Path | What it is |
|---|---|
| `src/Api` | Production API host (no development sign-in) |
| `src/Api.DevHost` | Development and Test host with development sign-in (ADR-0004) |
| `src/Workers` | Outbox dispatch, consumers, audit sealing and retention; `migrate` command |
| `src/BuildingBlocks` | Tenant context, persistence, messaging, localization, telemetry, cross-module contracts |
| `src/Modules` | Tenancy, Identity, Authorization, Entitlements, Audit, Administration |
| `src/Web` | Next.js 16 app (sign-in, admin shell), Arabic and English, RTL |
| `tests` | Unit, Integration, Security, Architecture suites and the shared two-tenant harness |
| `deploy` | Database bootstrap, local compose stack, Dockerfiles |
| `docs/architecture/r0-implementation-notes.md` | What R0 contains and where it differs from the plan |

## Quick start (local)
Requirements: .NET 10 SDK, Node 22, Docker (or a local PostgreSQL 16+ and RabbitMQ).

```bash
docker compose -f deploy/compose/compose.yaml up -d          # PostgreSQL with roles, RabbitMQ
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Api.DevHost --urls http://localhost:5080
cd src/Web && npm ci && TEAMBEATS_DEV_SIGNIN=true npm run dev  # http://<tenant>.teambeats.localhost:3000
```

The development host migrates the database and seeds a fictional platform operator (`operator@platform.example`). Sign in on `platform.teambeats.localhost` with `POST /dev/platform/signin`, create a tenant with `POST /api/v1/platform/tenants`, then sign in on `<key>.teambeats.localhost` as the appointed administrator. `*.localhost` resolves to your machine in modern browsers.

## Tests
```bash
export TEAMBEATS_TEST_PG_ADMIN="Host=localhost;Port=5432;Username=postgres;Password=postgres_dev_only"
dotnet test TeamBeats.slnx
cd src/Web && npm run verify
```
Each test assembly creates its own database with the `tb_owner` and `tb_app` roles, so tests run against real RLS. Regenerate the OpenAPI snapshot with `TEAMBEATS_UPDATE_OPENAPI=1` after a reviewed contract change.

## Status (2026-11-05)
- Review decisions 1 to 4: taken by Mohamed.
- Specs 001 to 007: Approved, each with `plan.md` and `tasks.md`. The Active Directory parts of 002 and 003 are pending approval; their plans and tasks are written.
- Specs 008, 009, 011, 013, 016, 020, 021, 022, 024, 025, 026, 027, 032, 033: Proposed. Spec 007 carries composable experience additions that need approval. Plans and tasks follow once each is approved.
- Constitution: v1.1.0, ratified by Mohamed (1.0.0 on 2026-11-04, 1.1.0 on 2026-11-05).
- ADR-0005, ADR-0013 and ADR-0014 Accepted. ADR-0001 to ADR-0004 and ADR-0006 to ADR-0012 Proposed. ADR-0009 (data access) is also carried by the R0 build pull request.
- R0 implemented for specs 001 to 007 (see `docs/architecture/r0-implementation-notes.md`).
- Next: approve the open ADRs and Proposed specs, then build R1 in the release order of `docs/product/roadmap.md`.
