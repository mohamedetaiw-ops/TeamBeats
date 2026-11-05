# R0 solution structure and shared building blocks

Status: Proposed (technical plan, follows ADR-0001 to ADR-0005)
Date: 2026-11-04

Every R0 plan (`specs/001` to `specs/007`) builds on this document. It defines the repository layout, shared building blocks, test harness and CI gates once, so each feature plan only describes its own module.

## 1. Repository layout

```
/src
  /Web                         Next.js 16 app (employee portal + admin shell)
  /Api                         ASP.NET Core host (production composition root)
  /Api.DevHost                 Development/Test host: references Api + Identity.DevAuth (ADR-0004)
  /Workers                     Worker host (outbox dispatcher, audit sealer, scheduled jobs)
  /BuildingBlocks
    /TenantContext             Tenant/org context, transaction-local set_config, RLS helpers
    /Persistence               EF Core base DbContext, interceptors, migration helpers
    /Messaging                 Outbox, inbox (dedupe), broker abstraction
    /Authorization.Contracts   IAuthorizationService, decision types (no implementation)
    /Entitlements.Contracts    IEntitlementService, capability keys
    /Audit.Contracts           IAuditWriter, audit event types
    /Localization              Resource catalogue access, locale resolution
    /Telemetry                 OpenTelemetry setup, correlation ID propagation
    /Primitives                IDs, clock, Result types
  /Modules
    /Tenancy                   spec 001 + 002 (tenant, org hierarchy, memberships)
    /Identity                  spec 003 (IdP config, external identity links, sessions)
    /Identity.DevAuth          spec 003 US1 (separate assembly, dev only)
    /Authorization             spec 004 (roles, assignments, resource permissions, evaluator)
    /Entitlements              spec 005
    /Audit                     spec 006 (audit store, search, export, sealer)
    /Administration            spec 006 admin shell read models
/tests
  /Unit
  /Integration                 Real PostgreSQL 18 via Testcontainers, RLS on
  /Architecture                Boundary and constitution rule tests
  /Security                    Cross-tenant, org boundary, auth protocol tests
  /Acceptance                  Playwright end-to-end, axe accessibility, RTL screenshots
/deploy
  /containers  /helm  /compose (dev only)
/tools
  /spec-lint                   Checks spec headers, status and traceability tables
```

Each module follows ADR-0013: `Domain/` (rules, no infrastructure references), `Application/` (use cases and the ports they need), `Adapters/` (endpoints, Npgsql persistence in its own schema, outbox publishing, external clients), and `Contracts/` (the only project other modules may reference). Module names and ownership come from `docs/architecture/module-map.md`. R0 modules were built flat and are converted when next changed.

## 2. Database conventions

- One PostgreSQL 18 database; one schema per module (`tenancy`, `identity`, `authz`, `entitlements`, `audit`).
- Roles: `tb_owner` (owns tables, runs migrations), `tb_app` (application, no ownership, no BYPASSRLS), `tb_operator` (break-glass, audited, R0 only stubbed).
- Every tenant-owned table: `tenant_id uuid NOT NULL`, primary keys `(tenant_id, id)` or `id` with a unique `(tenant_id, id)` for composite foreign keys, `ENABLE` and `FORCE ROW LEVEL SECURITY`, policy `tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid` for `USING` and `WITH CHECK`.
- Migration helper `EnableTenantRls(table)` generates the policy, so no table is created without it. An architecture test scans the catalog for tenant-owned tables missing RLS.
- Platform-level tables (tenant registry, package definitions, platform audit) are listed in `docs/architecture/rls-exceptions.md` with reasons.

## 3. Tenant context building block

- Resolved once per request from the authenticated session (tenant ID, user ID, membership ID). Never from route, query or body.
- An EF Core `DbConnectionInterceptor`/transaction interceptor runs `select set_config('app.tenant_id', @tid, true)` at the start of every transaction. All data access runs inside a transaction (enforced by the unit-of-work wrapper; queries outside one throw).
- Workers: each message carries `tenant_id` in headers; the consumer opens a transaction and sets context per message.
- Missing context means no rows (fail closed), verified by a test.

## 4. Messaging

- Transactional outbox table per module schema, dispatched by the worker host to RabbitMQ. Consumers use an inbox table for idempotency.
- Library choice needs **ADR-0006** before coding. Candidates: plain `RabbitMQ.Client` with an in-house outbox (small, no licence risk), or Wolverine (MIT). MassTransit v9 is commercial and is excluded unless an ADR accepts the licence (constitution XXII).
- R0 events: tenancy, authorization and entitlement change events, used for cache invalidation and audit sealing.

## 5. Web and API

- Backend-for-frontend pattern: the Next.js app and API share one origin per tenant host; the API issues the HTTP-only session cookie. No tokens in the browser.
- REST with OpenAPI generated from the API, versioned under `/api/v1`. OpenAPI diff check in CI.
- Problem Details (RFC 9457) for errors, with distinct `type` values for `authz.denied`, `entitlement.not_in_package`, `entitlement.quota_exhausted`, `tenant.suspended`.
- No mediator library. Use cases are plain handler classes. Mapping is explicit (no AutoMapper). Validation with FluentValidation (Apache 2.0).

## 6. Frontend

- Next.js 16 App Router, TypeScript strict.
- i18n with `next-intl` (MIT); catalogues in `src/Web/messages/{ar,en,fr,ur,hi}.json`.
- Styling with logical CSS properties only; a stylelint rule bans `left/right` physical properties and `margin-left` style declarations.
- ESLint rule banning literal JSX text (with allow list for punctuation).
- Design system primitives in `src/Web/components/ui` are the only place direction-specific icons are handled.
- From R1, the web app follows ADR-0014: `features/`, `components/ui` on Radix UI, `components/placements`, a single API client in `lib/api`, TanStack Query for interactive server state with tenant-keyed caches, Zustand only in the builder and personalization feature stores, and Tailwind only with token-bound themes and logical utilities.

## 7. Test harness (shared)

- `TenantTestFixture`: creates two tenants (A, B), each with a three-level org tree (Holding > Gulf, Africa > departments) and a set of users per role. Reused by every module.
- `CrossTenantSuite<TEndpoint>`: for every registered endpoint, calls it as a tenant A user with tenant B resource IDs and asserts not-found or denied with no data leakage. New endpoints fail CI until they are registered or explicitly exempted with a reason.
- `OrgBoundarySuite`: sibling, parent, child, subtree, single-unit cases.
- Architecture tests (ArchUnitNET or NetArchTest, both open source): module references only `Contracts`; no provider SDKs outside adapters; no role-name string checks; no package-name checks; no `IgnoreQueryFilters` outside allowed places.
- Playwright + axe for accessibility; screenshot tests in `ar` and `en`.

## 8. CI gates (R0)

1. Build, unit, integration, architecture, security suites.
2. Secret scan, dependency licence scan, SBOM.
3. OpenAPI diff.
4. Production artifact check: `Identity.DevAuth` assembly absent, startup guard test (ADR-0004).
5. Localization: missing keys for launch-complete locales (`ar`, `en`) fail the build.
6. `spec-lint`: every PR that changes `src/` references an Approved spec ID.

## 9. Open technical decisions for R0

| ADR | Decision | Needed before |
|---|---|---|
| ADR-0006 | Messaging library | spec 001 events |
| ADR-0007 | Server-side session store (PostgreSQL table vs Redis) | spec 003 |
| ADR-0008 | Audit hash chain sealing approach | spec 006 |
