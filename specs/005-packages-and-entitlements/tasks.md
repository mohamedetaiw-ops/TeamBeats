# Tasks: Packages and Entitlements

Depends on 001 Phase 1. R0 tasks first; R2 tasks in the last phase.

## Phase 1: Foundations (R0)
- [x] T001 Migration: `capability`, `package`, `package_version`, `package_capability` (platform, RLS exceptions), `subscription` (RLS)
- [x] T002 Seed capability catalogue and three package definitions v1 from spec section 6

## Phase 2: User Stories 1 and 2, gating and AI in every package (P1, R0)
- [x] T003 [P] [US1] Tests: disabled capability denied with `entitlement.not_in_package`; enabled passes to authorization; unknown key fails closed
- [x] T004 [P] [US2] Test: package definition without `ai.assistant` rejected
- [x] T005 [P] Tests: tenant admin cannot change own subscription; cross-tenant entitlement read denied
- [x] T006 [US1] `IEntitlementService.IsEnabled / GetLimit` with per-tenant cache and invalidation
- [x] T007 [US1] `[RequiresCapability]` endpoint filter and Problem Details types
- [x] T008 Platform endpoints: packages, publish version, set tenant subscription; tenant read-only endpoint

## Phase 3: User Story 5, versioning (P2, R0)
- [x] T009 [P] [US5] Test: tenants stay on v1 after v2 is published
- [x] T010 [US5] Version publish and explicit tenant migration

## Phase 4: R2
- [x] T011 [US3] Usage counters, atomic quota check with tolerance max(1%, 10), exhaustion notification once per period; deny, grace and warn enforcement; AI Gateway decided through it
- [x] T012 [US4] Add-ons, overrides with expiry, precedence resolution; platform routes, audit, `EntitlementsChanged`
- [x] T013 [US4] Expiry: 30-day grace, then read-only flag; contract route, state in the plan view, write refusal with `entitlement.expired`
- [x] T014 Plan and usage admin page (R2: add-ons and contract terms with dates, usage bar per quota, contract end with grace or read-only state)

## Final phase
- [ ] T015 Architecture test banning package keys in feature code; audit assertions; OpenAPI; spec evidence
