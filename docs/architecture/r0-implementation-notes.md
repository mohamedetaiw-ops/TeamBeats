# R0 implementation notes

What was built for R0, and where it differs from `r0-solution-structure.md`. Each difference is either covered by an ADR or listed as a follow-up.

## Built
- .NET 10 modular monolith: `src/Api` (production host), `src/Api.DevHost` (Development, Test and Staging host with development sign-in; Staging uses password mode for the staging test account, spec 003 US7), `src/Workers` (outbox dispatch, consumers, audit sealing and retention, `migrate` command).
- Modules: Tenancy (tenants, settings, lifecycle, org hierarchy, memberships), Identity (users, sessions, sign-in pipeline), Identity.DevAuth, Authorization, Entitlements, Audit, Administration (admin shell API).
- Building blocks: Primitives, TenantContext, Persistence, Messaging, Localization, Telemetry, and the Contracts projects for Authorization, Entitlements and Audit.
- Next.js 16 web app in `src/Web`: locale and direction from the API, sign-in page, server-driven admin shell, language switcher, lint gates.
- Tests: unit, integration (every R0 spec), security (RLS checks, cross-tenant route sweep, session requirement sweep), architecture, OpenAPI snapshot.
- CI: build and all suites on PostgreSQL 18, production artifact check, vulnerable package check, web verify and build, npm licence allow list, secret scan, SBOM, spec-lint.

## Differences from the planned layout
| Planned | Built | Why / status |
|---|---|---|
| EF Core with interceptors | Npgsql with `IDbSession` and SQL migrations | ADR-0009 (Accepted) |
| FluentValidation | Small `ValidationErrors` helper with resource keys | Validation rules are few and need resource keys, not messages. Revisit if forms grow. |
| Five projects per module (Domain, Application, Infrastructure, Api, Contracts) | One project per module plus a Contracts project | Same boundary enforcement (architecture tests check references) with far fewer projects. Split a module when it grows. |
| Outbox table per module schema | One `messaging.outbox` | Single dispatcher, same transactional guarantee. Listed in `rls-exceptions.md`. |
| Testcontainers | Real PostgreSQL from `TEAMBEATS_TEST_PG_ADMIN` (CI service container, local install or compose) | Docker is not available in every developer and agent environment. Each test assembly gets its own database. |
| S3-compatible object storage | File-system adapter behind `IObjectStorage`, tenant prefix enforced by `TenantObjectStorage` | S3 adapter with MinIO in compose arrives in R1. |
| Audit table partitioning | Not partitioned | Add monthly partitions before volumes grow (R1). |
| Session cache | Every request reads the session row | Makes revocation immediate (ADR-0007). Add a short cache only if load needs it. |
| Playwright + axe, RTL screenshots | Not yet | Follow-up for spec 007 T013 and T014. Lint gates for literal strings and physical CSS are in place. |
| Self-hosted fonts (spec 007 T003) | System font stacks | Follow-up. |
| Group membership changes audited as their own action | Audited as `authz.role_changed` | Spec 004 does not name a group action. Recorded as a gap. |
| Audience targeting | Not built | Belongs with the content specs (R1). |

## Licence exceptions
- `@img/sharp-libvips-*` (LGPL-3.0-or-later): native image library pulled in by Next.js through `sharp`, an optional dependency used for image optimization. It is OSI approved, unmodified and dynamically loaded, so it is excluded from the strict npm allow list in CI. If the owner prefers no LGPL at all, set `images.unoptimized` and drop `sharp` from the production image.

## Running locally
See the README quick start.
