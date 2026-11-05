# TeamBeats

Spec-driven development repository for TeamBeats (working name), an AI-native enterprise intranet. Built from `AI_Intranet_Product_Foundation` v0.1.

The layout follows GitHub spec-kit conventions so `/specify`, `/clarify`, `/plan` and `/tasks` style workflows work.

| Path | What it is |
|---|---|
| `docs/review/foundation-v0.1-review.md` | Critical review of the foundation, with the decisions needed |
| `.specify/memory/constitution.md` | The constitution (v1.0.0, ratified) |
| `.specify/templates/` | Spec, plan, tasks and ADR templates |
| `specs/` | Feature specs 001 to 007 with plans and tasks, R0 build order, and the index of planned specs |
| `docs/architecture/r0-solution-structure.md` | Shared R0 repo layout, building blocks, test harness and CI gates |
| `docs/architecture/decisions/` | ADR-0001 to ADR-0009 (ADR-0005 Accepted, others Proposed) |
| `docs/product/roadmap.md` | Release slices R0 to R3 (adopted) |
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

## Status (2026-11-04)
- Review decisions 1 to 4: taken by Mohamed.
- Specs 001 to 007: Approved, each with `plan.md` and `tasks.md`.
- Constitution: v1.0.0, ratified by Mohamed on 2026-11-04.
- ADR-0001 to ADR-0004 Proposed, ADR-0005 Accepted, ADR-0006 to ADR-0009 Proposed.
- R0 implemented for specs 001 to 007 (see `docs/architecture/r0-implementation-notes.md`). Merging the R0 pull request accepts ADR-0009.
