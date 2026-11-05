# Implementation Plan: Organization Hierarchy and Memberships

**Spec:** `specs/002-organization-hierarchy/spec.md` (v1.0, Approved) | **Branch:** `002-organization-hierarchy` | **Date:** 2026-11-04
**Shared base:** `docs/architecture/r0-solution-structure.md`

## Summary
Adjacency list plus closure table in the `Tenancy` module, maintained transactionally. Memberships with primary flag and validity dates. Org admin scope checks through the Authorization contract. Publishes events consumed by Authorization for cache invalidation.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I Tenant boundary | Yes | All tables tenant-owned with RLS; composite FKs `(tenant_id, parent_id)` make cross-tenant parents impossible at the database level |
| II Org scope | Yes | Structure grants nothing (FR-009); org admin scope validated for source and destination on moves |
| VI Audit | Yes | All changes audited |
| XIV Boundaries | Yes | Authorization reads hierarchy through `IOrganizationHierarchy` contract, not tables |
| XXIII Localization | Yes | Localized names table |

## Data model (schema `tenancy`)
- `org_unit`: `tenant_id`, `id`, `parent_id null`, `type_id`, `code`, `status`, `external_source`, `external_id`. Unique `(tenant_id, code)`, unique `(tenant_id, external_source, external_id)`. FK `(tenant_id, parent_id) -> org_unit(tenant_id, id)`.
- `org_unit_name`: `tenant_id`, `org_unit_id`, `locale`, `name`.
- `org_unit_type`: built-in rows (platform) + tenant custom types (tenant-owned).
- `org_closure`: `tenant_id`, `ancestor_id`, `descendant_id`, `depth`. PK `(tenant_id, ancestor_id, descendant_id)`; index `(tenant_id, descendant_id)`.
- `org_membership`: `tenant_id`, `id`, `user_id`, `org_unit_id`, `type`, `is_primary`, `valid_from`, `valid_to null`. Partial unique index: one active primary per `(tenant_id, user_id)`.

## Algorithms
- Insert: closure rows = ancestors of parent + self (depth 0).
- Move: in one transaction, lock subtree rows (`FOR UPDATE`), reject if new parent is in subtree (cycle) or depth would exceed 12, delete closure rows linking old ancestors to subtree, insert cross product of new ancestors and subtree. Single SQL statements; 1,000-unit subtree well under 5 s.
- Depth limit enforced on insert and move.

## API (v1)
| Method | Path |
|---|---|
| GET | `/api/v1/org/units?parentId=&depth=` (tree slices) |
| POST | `/api/v1/org/units` |
| PATCH | `/api/v1/org/units/{id}` (rename, type) |
| POST | `/api/v1/org/units/{id}:move` `{ newParentId }` |
| POST | `/api/v1/org/units/{id}:deactivate` `{ childrenAction, membershipsAction }` |
| GET/POST | `/api/v1/org/units/{id}/memberships` |
| POST | `/api/v1/org/memberships/{id}:end` |

Cross-tenant IDs return 404 (indistinguishable from not found).

## Contracts published
- `IOrganizationHierarchy.IsAncestorOrSelf(tenant, a, b)`, `GetDescendants(tenant, a)`, `GetActiveMemberships(tenant, user)`.
- Events: `OrganizationUnitCreated`, `OrganizationUnitMoved`, `OrganizationUnitDeactivated`, `OrganizationMembershipChanged`.

## Tests
- Cycle (direct, indirect), cross-tenant parent, depth 13 rejected, duplicate code, 20,000-unit performance test (ancestor query p95 < 50 ms), move of 1,000 units < 5 s.
- Org admin scope: move inside scope ok, into sibling denied, out of scope source denied.
- Membership: primary uniqueness, membership requires tenant membership, ended membership not active.
- Warning (not error) for unusual type placement.

## Directory mapping (R1 Entra ID, R2 on-premises AD; starts after US5 and FR-010 to FR-014 are approved)
- Owner: `Tenancy` module. Directory reads come from the `Identity` module's directory connection (spec 003) through a published contract `IDirectoryReader` (units, attribute values, group lists, deltas). Tenancy never calls Microsoft Graph or LDAPS itself (constitution XVII).
- Data model additions (schema `tenancy`): `org_unit.owner (product, directory, hr)` and `owner_connection_id`; `org_membership.owner` and `owner_connection_id`; `directory_mapping` (`tenant_id`, `connection_id`, `strategy (ou_path, attributes, admin_units, groups)`, `base_dn`, `attributes jsonb`, `alias_rules jsonb`, `deletion_threshold_pct`, `schedule_minutes`); `directory_sync_run` (`tenant_id`, `id`, `connection_id`, `mode (preview, apply)`, `started_at`, `finished_at`, `outcome`, `counts jsonb`, `warnings jsonb`, `delta_token_ref`); `directory_unmapped` (users and values the mapping could not place).
- Sync job: one message per tenant and connection on the outbox, with tenant context set by the worker. Steps: read delta from `IDirectoryReader`, compute the diff against directory-owned rows, check the deletion threshold, then apply in one transaction per batch of 500 changes. Preview runs the same diff and stores it without applying.
- Normalization for attribute strategy: trim, collapse spaces, case-fold, Unicode NFKC, then alias rules. The normalized path is the `external_id`.
- Ownership guard: rename, move and end endpoints refuse rows whose `owner` is not `product` (409 with the owning source in the body).
- HR versus directory: a tenant setting `hierarchy_source (product, directory:<connection>, hr)`; only that source may create or move units (FR-014).
- Events: `DirectorySyncCompleted` with counts; reuse `OrganizationUnitMoved` and `OrganizationMembershipChanged` for each applied change.
- Tests: each strategy on a fixture directory (3 companies, 12 departments, 200 users); rename and move picked up by delta; case and alias normalization; disappearing value with children is flagged, not deactivated; threshold breach stops the run with nothing applied; directory-owned rows refuse product edits; product-only units untouched; two connections with the same OU names do not collide; a sync for tenant A never writes tenant B rows; org admin can read but not change the mapping.
