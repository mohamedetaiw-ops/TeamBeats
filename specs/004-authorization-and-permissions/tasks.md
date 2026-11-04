# Tasks: Authorization and Permissions

Depends on 001 Phase 1 and 002 Phase 1.

## Phase 1: Foundations
- [ ] T001 Migration: `permission`, `role`, `role_permission`, `role_assignment`, `resource_acl`, `inheritance_rule`, `membership_type_policy`, `group`, `group_member` (RLS where tenant-owned)
- [ ] T002 Seed built-in roles (Tenant Administrator, Organization Administrator, Content Editor, Content Publisher, Workflow Designer, Approver, Integration Administrator, AI Policy Administrator, Security Officer, Employee) and permission catalogue
- [ ] T003 `IResourceDescriptorProvider` contract and a test resource type for boundary tests

## Phase 2: User Stories 1 and 2, scoped roles and no implicit inheritance (P1)
- [ ] T004 [P] [US1] Boundary matrix tests: subtree, single unit, sibling, parent, child
- [ ] T005 [P] [US2] Tests: parent membership grants nothing; explicit inheritance rule grants and is named in explanation
- [ ] T006 [US1] Effective grants loader and evaluator; `IAuthorizationService` implementation
- [ ] T007 [US1] Per-instance cache with 60 s TTL and event eviction

## Phase 3: User Story 3, resource permissions (P1)
- [ ] T008 [P] [US3] Tests: group grant allows outside unit; revocation effective; IDOR returns 404
- [ ] T009 [US3] ACL endpoints and evaluator support

## Phase 4: User Story 4, targeting vs access (P1)
- [ ] T010 [P] [US4] Tests with the test resource type: targeted-but-readable vs restricted
- [ ] T011 [US4] Document the targeting model contract for content specs (no targeting storage here)

## Phase 5: Batch filtering
- [ ] T012 [P] Performance tests: single decision < 5 ms, batch of 1,000 < 50 ms
- [ ] T013 `FilterVisible` and SQL predicate builder `VisibleTo(user)`

## Phase 6: User Story 5, delegation (P2)
- [ ] T014 [P] [US5] Tests: delegable role in scope allowed; tenant admin role denied; out-of-scope denied; cannot grant more than held
- [ ] T015 [US5] Assignment endpoints with delegation rules; custom roles gated by `authz.custom_roles`

## Phase 7: User Story 6, explain (P3) and UI
- [ ] T016 Explain endpoint and `/me/access`
- [ ] T017 Role assignment UI with scope picker; explain view

## Final phase
- [ ] T018 Membership type policy (remove default Employee role per type) with tests
- [ ] T019 Audit assertions; architecture test banning role-name checks; OpenAPI; spec evidence
