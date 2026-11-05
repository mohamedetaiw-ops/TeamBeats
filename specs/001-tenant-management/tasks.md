# Tasks: Tenant Management (and R0 platform setup)

**Input:** `spec.md`, `plan.md`, `docs/architecture/r0-solution-structure.md`
Format: `[ID] [P?] [Story] Description`. Tests come first and must fail before implementation.
Phase 1 here is the shared R0 setup. Specs 002 to 007 depend on it.

## Phase 1: R0 platform setup (shared)
- [ ] T001 Write ADR-0006 (messaging library), ADR-0007 (session store), ADR-0008 (audit sealing); get approval
- [ ] T002 Create solution and project layout per r0-solution-structure section 1 (`src/`, `tests/`, `deploy/`, `tools/`)
- [ ] T003 [P] Docker Compose dev stack: PostgreSQL 18 with pgvector, RabbitMQ, MinIO (dev only)
- [ ] T004 [P] CI pipeline skeleton: build, test, secret scan, licence scan, SBOM, OpenAPI diff
- [ ] T005 [P] Next.js 16 app skeleton with TypeScript strict, ESLint, stylelint
- [ ] T006 Database roles `tb_owner`, `tb_app`, `tb_operator` and migration runner using `tb_owner`
- [ ] T007 TenantContext building block: session-derived context, transaction interceptor with `set_config(..., true)`, fail-closed when missing
- [ ] T008 Migration helper `EnableTenantRls(table)` and `rls-exceptions.md`
- [ ] T009 Outbox and inbox building block (per ADR-0006)
- [ ] T010 Telemetry building block: OpenTelemetry, correlation ID across HTTP and messages
- [ ] T011 Contracts projects: Authorization, Entitlements, Audit (interfaces only, in-memory fakes for tests)
- [ ] T012 Test harness: `TenantTestFixture` (tenants A and B, org trees, role users), `CrossTenantSuite`, `OrgBoundarySuite`
- [ ] T013 Architecture tests: module references, no RLS-less tenant tables, no role-name checks, no package-name checks, no provider SDKs outside adapters
- [ ] T014 [P] `tools/spec-lint`: spec headers, status, traceability table

## Phase 2: Foundations for tenant management
- [ ] T015 Migration: `tenancy.tenant` (RLS exception), `tenant_settings`, `tenant_admin_appointment` with RLS
- [ ] T016 Host-based tenant resolution helper (subdomain in shared installs, single host in dedicated)

## Phase 3: User Story 1, create a tenant (P1)
### Tests first
- [ ] T017 [P] [US1] Integration test: create tenant happy path (roles seeded, package assigned, pending admin, audit event)
- [ ] T018 [P] [US1] Test: duplicate key case-insensitive returns 409; concurrent create, exactly one succeeds
- [ ] T019 [P] [US1] Test: unknown package rolls back everything; reserved and invalid keys rejected
- [ ] T020 [P] [US1] Security test: tenant admin calling platform endpoint denied
### Implementation
- [ ] T021 [US1] Tenant aggregate, key rules, status model
- [ ] T022 [US1] `POST /api/v1/platform/tenants` with idempotency key, one transaction through Authorization and Entitlements contracts
- [ ] T023 [US1] `GET /api/v1/platform/tenants` list and search
- [ ] T024 [US1] Platform admin UI: tenant list and create form (localized)

## Phase 4: User Story 2, branding and locales (P2)
- [ ] T025 [P] [US2] Tests: enable ar+en with ar default; cannot disable default locale; cross-tenant tampering denied
- [ ] T026 [P] [US2] Tests: logo PNG/JPEG/SVG up to 1 MB accepted; SVG with script rejected; 1.1 MB rejected
- [ ] T027 [US2] `GET/PUT /api/v1/tenant/settings`; SVG sanitizer; logo storage under `tenants/{tenant_id}/branding/`
- [ ] T028 [US2] Tenant settings page with LTR and RTL branding preview and contrast warning

## Phase 5: User Story 3, suspend and reactivate (P2)
- [ ] T029 [P] [US3] Tests: suspended tenant sign-in refused, sessions revoked, worker skips messages; reactivation restores
- [ ] T030 [US3] Suspend, reactivate, close endpoints; `deletion_due_at = now + 90 days`
- [ ] T031 [US3] Shared consumer filter that checks tenant status before processing

## Final phase
- [ ] T032 Audit assertions for all spec section 13 events
- [ ] T033 OpenAPI updated; spec status set to In Progress, then Implemented with evidence
