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
