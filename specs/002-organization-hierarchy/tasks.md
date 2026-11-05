# Tasks: Organization Hierarchy and Memberships

Depends on 001 Phase 1. Tests first.

## Phase 1: Foundations
- [ ] T001 Migration: `org_unit`, `org_unit_name`, `org_unit_type`, `org_closure`, `org_membership` with RLS and composite FKs
- [ ] T002 Seed built-in unit types (Organization, Subsidiary, Division, Department, Business Unit)
- [ ] T003 `IOrganizationHierarchy` contract implementation

## Phase 2: User Story 1, build the hierarchy (P1)
- [ ] T004 [P] [US1] Tests: create roots and children; duplicate code rejected; same code in other tenant allowed
- [ ] T005 [P] [US1] Tests: direct and indirect cycle rejected; cross-tenant parent returns 404; depth 13 rejected
- [ ] T006 [P] [US1] Test: unusual type placement returns a warning, not an error
- [ ] T007 [US1] Create and rename use cases with closure maintenance; localized names
- [ ] T008 [US1] Endpoints `GET/POST/PATCH /api/v1/org/units`

## Phase 3: User Story 3, memberships (P1)
- [ ] T009 [P] [US3] Tests: two memberships with one primary; requires tenant membership; ended membership inactive
- [ ] T010 [US3] Membership use cases and endpoints; `OrganizationMembershipChanged` event

## Phase 4: User Story 2, move a subtree (P2)
- [ ] T011 [P] [US2] Tests: move updates closure atomically; org admin cannot move into sibling scope; audit and event emitted
- [ ] T012 [P] [US2] Performance test: 1,000-unit move under 5 s; ancestor query p95 under 50 ms at 20,000 units
- [ ] T013 [US2] Move use case with subtree locking and depth check

## Phase 5: User Story 4, deactivate (P3)
- [ ] T014 [P] [US4] Tests: rejected with active children unless action chosen; memberships must be ended or moved
- [ ] T015 [US4] Deactivate use case

## Phase 6: UI
- [ ] T016 Org tree admin page (WAI-ARIA tree, keyboard navigation, RTL mirrored), move dialog with affected count
- [ ] T017 Membership management panel

## Phase 7: User Story 5, directory mapping (P1, R1 Entra ID, R2 on-premises AD; after approval of US5)
- [ ] T019 Migration: owner columns on `org_unit` and `org_membership`; `directory_mapping`, `directory_sync_run`, `directory_unmapped` with RLS
- [ ] T020 [P] [US5] Tests: preview for each strategy changes no data; apply stores `external_source` and `external_id`; one primary membership per mapped user
- [ ] T021 [P] [US5] Tests: directory-owned unit and membership refuse rename, move and end; product-only units never changed by sync
- [ ] T022 [P] [US5] Tests: mover ends old membership and starts new one with history; disappearing value with children is flagged; case, spacing and alias normalization
- [ ] T023 [P] [US5] Tests: deletion threshold stops the run before any change; cross-tenant sync isolation; org admin read-only on mapping
- [ ] T024 [US5] `IDirectoryReader` consumer, diff engine, threshold check and batched apply on the outbox worker
- [ ] T025 [US5] Mapping page (strategy picker, preview diff, unmapped list, last runs) and "Managed by Active Directory" badge
- [ ] T026 [US5] HR versus directory hierarchy source setting (FR-014) with tests
- [ ] T027 [US5] Performance test: first full sync of 20,000 users and 2,000 units under 30 minutes; incremental run of 500 changes under 2 minutes

## Final phase
- [ ] T018 Audit assertions (including `org.directory_mapping_changed` and `org.directory_sync_completed / halted`); OpenAPI; spec status evidence
