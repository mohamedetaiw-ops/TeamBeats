# Implementation Plan: Authorization and Permissions

**Spec:** `specs/004-authorization-and-permissions/spec.md` (v1.0, Approved) | **Branch:** `004-authorization` | **Date:** 2026-11-04
**Shared base:** `docs/architecture/r0-solution-structure.md`

## Summary
An `Authorization` module that owns roles, permissions, assignments, resource ACLs and inheritance rules, and implements `IAuthorizationService` from `Authorization.Contracts`. Decisions are computed from a per-user "effective grants" snapshot cached per instance, invalidated by events, with a 60-second maximum staleness.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II | Yes | All tables tenant-owned with RLS; no inheritance without explicit subtree scope or rule |
| III | Yes | Single evaluator; architecture test bans `IsInRole`, `User.IsInRole`, and string role comparisons outside this module |
| VI | Yes | Role, assignment, ACL and rule changes audited |
| XIV | Yes | Hierarchy read via `IOrganizationHierarchy`; resources registered by owning modules through a contract |

## Data model (schema `authz`)
- `permission` (platform catalogue): `key` such as `content.publish`, `resource_type`, `action`.
- `role`: `tenant_id null` (null = built-in), `id`, `key`, `is_delegable`, `is_custom`.
- `role_permission`: role to permission keys.
- `role_assignment`: `tenant_id`, `id`, `subject_type (user, group)`, `subject_id`, `role_id`, `scope_type (tenant, org_unit, org_subtree, resource)`, `scope_id`, `granted_by`.
- `resource_acl`: `tenant_id`, `resource_type`, `resource_id`, `subject_type (user, group, org_unit, org_subtree)`, `subject_id`, `action`.
- `inheritance_rule`: `tenant_id`, `org_unit_id`, `rule (members_view_descendant_restricted)`, `enabled`.
- `membership_type_policy`: `tenant_id`, `membership_type`, `default_employee_role bool` (supports removing default Employee role for contractors).
- `group` and `group_member`: product groups; IdP groups are mapped into assignments at sign-in.

## Evaluation
1. Verify active tenant membership (from session context).
2. Load effective grants for user: direct and group assignments with scopes, expanded org subtrees (via closure), applicable inheritance rules, default Employee role if policy allows.
3. Decision for `(action, resource)`: resource's owning org unit and ACL are provided by the owning module through `IResourceDescriptorProvider`. Allow if any grant covers action at tenant, matching unit, ancestor subtree, or resource ACL. Union only (no deny in R0).
4. Return `Decision { Allowed, Reasons[] }`. Reasons power the "explain" endpoint.

Batch filter: `FilterVisible(resourceType, ids)` and a SQL predicate builder (`VisibleTo(user)`) that modules can compose into list queries, so search and feeds do not loop per item.

## Caching
- Per-instance memory cache keyed `(tenant_id, user_id, grants_version)`, TTL 60 s.
- `RoleAssignmentChanged`, `ResourcePermissionChanged`, `OrganizationUnitMoved`, `OrganizationMembershipChanged` events evict immediately.

## Delegation rules
Assigner must hold `authz.assign` at a scope that covers the target scope, the role must be delegable, and the role's permissions must be a subset of the assigner's own.

## API (v1)
`GET/POST /api/v1/authz/roles`, `GET/POST/DELETE /api/v1/authz/assignments`, `GET/PUT /api/v1/authz/resources/{type}/{id}/acl`, `GET /api/v1/authz/explain?userId=&resourceType=&resourceId=&action=`, `GET /api/v1/me/access`.

## Tests
- Full boundary matrix from spec US1 and US2 (sibling, parent, child, subtree, single unit) using the shared fixture.
- Targeting vs access (US4) with a stub content resource type.
- Delegation escalation attempts (US5).
- Revocation effective within 60 s and immediately after event.
- IDOR returns 404. Performance: single decision < 5 ms, batch of 1,000 < 50 ms.
- Custom roles denied on Workplace package (entitlement `authz.custom_roles`).
