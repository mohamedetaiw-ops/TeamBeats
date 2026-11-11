# Tasks: Packages and Entitlements

Depends on 001 Phase 1. R0 tasks first; R2 tasks in the last phase.

## Phase 1: Foundations (R0)
- [ ] T001 Migration: `capability`, `package`, `package_version`, `package_capability` (platform, RLS exceptions), `subscription` (RLS)
- [ ] T002 Seed capability catalogue and three package definitions v1 from spec section 6

## Phase 2: User Stories 1 and 2, gating and AI in every package (P1, R0)
- [ ] T003 [P] [US1] Tests: disabled capability denied with `entitlement.not_in_package`; enabled passes to authorization; unknown key fails closed
- [ ] T004 [P] [US2] Test: package definition without `ai.assistant` rejected
- [ ] T005 [P] Tests: tenant admin cannot change own subscription; cross-tenant entitlement read denied
- [ ] T006 [US1] `IEntitlementService.IsEnabled / GetLimit` with per-tenant cache and invalidation
- [ ] T007 [US1] `[RequiresCapability]` endpoint filter and Problem Details types
- [ ] T008 Platform endpoints: packages, publish version, set tenant subscription; tenant read-only endpoint

## Phase 3: User Story 5, versioning (P2, R0)
- [ ] T009 [P] [US5] Test: tenants stay on v1 after v2 is published
- [ ] T010 [US5] Version publish and explicit tenant migration

## Phase 4: R2
- [ ] T011 [US3] Usage counters, atomic quota check with tolerance max(1%, 10), exhaustion notification once per period
- [ ] T012 [US4] Add-ons, overrides with expiry, precedence resolution
- [ ] T013 [US4] Expiry: 30-day grace, then read-only flag
- [x] T014 Plan and usage admin page

## Final phase
- [ ] T015 Architecture test banning package keys in feature code; audit assertions; OpenAPI; spec evidence
