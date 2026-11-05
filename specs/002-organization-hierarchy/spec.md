# Feature Specification: Organization Hierarchy and Memberships

| Field | Value |
|---|---|
| Spec ID | 002-organization-hierarchy |
| Area | platform |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R0 (manual hierarchy), R1 (Entra ID directory mapping), R2 (on-premises Active Directory mapping) |
| Last updated | 2026-11-05 |
| Approved | 2026-11-04 by Mohamed |
| Requirement IDs | TEN-003, TEN-004, TEN-005, TEN-006, TEN-010, INT-007 (directory part) |
| Use cases | UC-02 |
| Depends on | 001, 003 (directory connection), 004 (scoped admin roles), 006 (audit), 017 (connector framework, for on-premises AD) |

## 1. Problem and outcome

**Problem:** Customers are groups of companies, not single companies. A holding company may have subsidiaries that must be run separately but sit under one contract. Modelling each subsidiary as a separate tenant breaks shared services; modelling them as one flat tenant leaks data between them.

**Outcome:** Each tenant has a hierarchy of organization units (parent organizations, subsidiaries, divisions, departments, business units). Users can belong to one or more units. The hierarchy is available to navigation, targeting, authorization and reporting. Tenants that already keep their structure in Active Directory can map it from there instead of typing it in, and keep it in step automatically.

## 2. Scope

**In scope:**
- Create, rename, move, deactivate organization units.
- Built-in unit types plus tenant-defined types.
- Organization memberships (a user in one or more units, with a membership type).
- Preferred (home) organization per user.
- Cycle prevention and cross-tenant reference prevention.
- Bulk import from a file. (Decided 2026-11-04: CSV import is delivered in R1, not R0.)
- Mapping units and memberships from Active Directory: Microsoft Entra ID through Microsoft Graph (R1), and on-premises Active Directory Domain Services over LDAPS (R2). The directory connection itself is configured in spec 003.

**Out of scope:**
- HR-driven synchronization (UC-09, spec 019). HR and directory sources follow the same mapping and ownership rules in this spec.
- Permission inheritance rules (spec 004 owns these; this spec only provides the structure).
- Org chart of people by reporting line (spec 010). Reporting line is not the same as organization unit membership. The `manager` attribute read during directory sync is handed to spec 010 and is not stored as a membership.

## 3. Actors and permissions

| Actor | Can do | Scope |
|---|---|---|
| Tenant administrator | Manage the whole hierarchy and all memberships | Tenant |
| Organization administrator | Manage units and memberships within their assigned unit or subtree | Assigned unit or subtree |
| Employee | View the units they are allowed to see | Per spec 004 |

## 4. User scenarios

### User Story 1: Build the hierarchy (Priority: P1)

A tenant admin creates "Acme Holding" as a parent organization, adds subsidiaries "Acme Gulf" and "Acme Africa", and departments under each.

**Independent test:** build a three-level tree and read it back with ancestors and descendants.

**Acceptance scenarios:**
1. **Given** tenant A, **When** the tenant admin creates units with valid parents, **Then** each unit has a tenant-unique code, a type, a parent (or none for a root) and status Active.
2. **Given** unit X is an ancestor of unit Y, **When** anyone tries to set X's parent to Y, **Then** the move is rejected as a cycle. *(TEN-005)*
3. **Given** a unit in tenant A, **When** a request tries to set its parent to a unit in tenant B, **Then** it is rejected and the response does not reveal whether the tenant B unit exists. *(TEN-005)*
4. **Given** a tenant with several roots, **When** the tree is listed, **Then** all roots and their descendants are returned. Multiple roots are allowed.

### User Story 2: Move a subtree (Priority: P2)

After a restructure, the "Logistics" division moves from "Acme Gulf" to "Acme Holding".

**Acceptance scenarios:**
1. **Given** a subtree of 200 units, **When** it is moved, **Then** all ancestor/descendant relations update in one transaction and are visible immediately in subsequent reads.
2. **Given** the move, **Then** an audit event records old parent, new parent and actor, and an `OrganizationUnitMoved` event is published so that dependent features (targeting, permissions, search metadata) can refresh.
3. **Given** an organization admin scoped to "Acme Gulf", **When** they try to move a unit into "Acme Africa", **Then** it is denied because the destination is outside their scope.

### User Story 3: Manage memberships (Priority: P1)

An employee works for "Acme Gulf Finance" and is also seconded to "Acme Holding Strategy".

**Acceptance scenarios:**
1. **Given** a user with a tenant membership, **When** an admin adds two organization memberships, **Then** both are stored, one is marked primary, and the user's preferred unit defaults to the primary. *(TEN-006)*
2. **Given** a user without tenant membership in tenant A, **When** an admin of tenant A tries to add them to a unit, **Then** it is rejected.
3. **Given** a membership, **When** it is ended, **Then** it stays in history with an end date and stops counting for access from that moment.

### User Story 4: Deactivate a unit (Priority: P3)

**Acceptance scenarios:**
1. **Given** a unit with active child units, **When** an admin deactivates it without choosing what happens to children, **Then** the action is rejected with a list of active children.
2. **Given** a unit with active memberships, **When** it is deactivated, **Then** the admin must choose to end or move the memberships.
3. Units are never hard-deleted while referenced by content, memberships, audit or permissions.

### User Story 5: Map the hierarchy from Active Directory (Priority: P1, R1 for Entra ID, R2 for on-premises AD)

Acme keeps companies and departments in Active Directory. The tenant admin connects the directory, picks how units are derived, previews the result, and from then on joiners, movers and leavers in AD show up in TeamBeats without manual work.

**Independent test:** connect a test directory with 3 companies, 12 departments and 200 users; run a preview, apply it, then move one user and rename one department in the directory and confirm the next sync reflects both.

**Acceptance scenarios:**
1. **Given** a working directory connection (spec 003), **When** the tenant admin chooses a mapping strategy, **Then** the product offers: (a) OU path, where each organizational unit in the chosen base DN becomes a unit; (b) attributes, where `company` becomes the top level and `department` (and optionally `division`) the levels below; (c) Entra ID administrative units; (d) groups, where selected groups become units. Exactly one strategy is active per directory connection.
2. **Given** a strategy, **When** the admin runs a preview, **Then** the product shows units to create, rename, move and deactivate, memberships to add and end, and values it could not map, without changing any data.
3. **Given** an approved preview, **When** the sync applies it, **Then** every created unit stores the directory as `external_source` and a stable directory key (objectGUID for OUs, the normalized attribute value path for attribute mapping) as `external_id`, and each mapped user gets one primary membership in the unit their record maps to.
4. **Given** a unit or membership created by the directory, **When** a tenant admin tries to rename, move or end it in the product, **Then** the change is refused with a message naming the directory as the owner. Product-only units (for example a project office that does not exist in AD) stay fully editable and the sync never touches them.
5. **Given** a user moved to another department in AD, **When** the next sync runs, **Then** the old directory-owned primary membership ends with today's date, the new one starts, history is kept, and `OrganizationMembershipChanged` is published so access and targeting refresh.
6. **Given** a directory OU or attribute value that disappears, **When** the sync runs, **Then** the matching unit is deactivated only if it has no active children or memberships left; otherwise it is flagged for the admin and left active.
7. **Given** attribute values that differ only in case, spacing or language (for example "Finance" and "finance "), **When** mapping runs, **Then** they map to one unit. The admin can add alias rules (for example "HR" and "Human Resources") and can map a value to an existing product unit instead of creating a new one.
8. **Given** a sync that would deactivate more than the safety threshold of units or end more than the threshold of memberships in one run (default 10%), **When** it runs, **Then** it stops before applying, keeps the last good state and alerts the tenant admin.
9. **Given** an organization admin scoped to "Acme Gulf", **When** they open the directory mapping, **Then** they can see the mapping result for their subtree but cannot change the mapping strategy, which is a tenant admin action.

### Edge cases
- Depth limit. (Decided 2026-11-04: maximum 12 levels.)
- Duplicate codes within a tenant: rejected. Same code in different tenants: allowed.
- Renaming a unit keeps its ID and code; localized names supported per enabled locale.
- External identifiers (from HR or a directory) are unique per tenant and source system.
- A user in AD with no value for the mapped attribute or outside the mapped base DN: the user is synced (spec 003) but gets no directory membership, and appears in an "unmapped users" list for the admin.
- Two directory connections in one tenant (for example two AD forests after an acquisition): each has its own `external_source`, so the same OU name in both does not collide.
- Arabic and English unit names: the directory value becomes the name in the tenant default locale; other locale names are added in the product and survive later syncs.

## 5. Functional requirements

- **FR-001**: A tenant MUST contain zero or more organization units in a parent-child hierarchy with one or more roots. *(TEN-003)*
- **FR-002**: Built-in unit types MUST include Organization, Subsidiary, Division, Department, Business Unit. Tenants MAY add custom types. *(TEN-004)*
- **FR-003**: The system MUST reject any change that would create a cycle or reference a unit in another tenant. *(TEN-005)*
- **FR-004**: The system MUST answer "is X an ancestor of Y" and "list all descendants of X" efficiently for trees up to the design-to scale. (Decided 2026-11-04: design-to scale is 20,000 units per tenant.)
- **FR-005**: A user MUST be able to hold multiple organization memberships in a tenant, each with a type (primary, secondary, secondment, other), start date and optional end date. *(TEN-006)*
- **FR-006**: Organization membership MUST require an active tenant membership.
- **FR-007**: Unit names MUST support localized variants for enabled locales with fallback to the tenant default locale.
- **FR-008**: All hierarchy and membership changes MUST be audited and publish domain events. *(TEN-010)*
- **FR-009**: Organization structure MUST NOT by itself grant any permission. Permissions come from spec 004.
- **FR-010**: The hierarchy MUST be mappable from a directory source (Entra ID in R1, on-premises AD DS in R2) using one of the strategies in US5, with preview before the first apply and before any mapping change.
- **FR-011**: Every unit and membership MUST record its owner: product, directory (with connection ID) or HR. Only the owning source may change source-owned fields. Product-owned items MUST never be changed by a sync.
- **FR-012**: Directory sync MUST be incremental after the first full run (delta queries for Entra ID, `uSNChanged` or DirSync for AD DS), idempotent, and safe to replay.
- **FR-013**: A sync run MUST stop without applying when it exceeds the configured deletion threshold, and MUST record its counts, warnings and outcome.
- **FR-014**: When both HR (spec 019) and a directory are connected, the tenant admin MUST choose one of them as the source of the hierarchy. The other can still supply user attributes but does not create or move units.

## 6. Business and validation rules
- Exactly one primary membership per user per tenant when the user has any memberships.
- A unit's type does not restrict where it can sit in the tree. (Decided 2026-11-04: no ordering rule; the UI shows a warning for unusual placements.)
- Directory-created units get a generated code (`ad-` plus a short stable hash of the external ID) that the admin may replace once; the external ID, not the code, is the sync key.
- The sync schedule defaults to every 30 minutes, with a manual "sync now" for tenant admins. A leaver disabled in the directory is handled by spec 003 at the next run.

## 7. Tenant and organization scope
- All entities tenant-owned with RLS.
- Cross-tenant denial: read unit, set parent, add membership, list tree.
- Org boundary: org admin of A cannot read or change sibling B's units or memberships unless granted.
- A directory connection belongs to exactly one tenant. A sync job runs with that tenant's context set and cannot write rows for another tenant, even if two tenants point at the same directory.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Organization unit | Product, directory or HR (per unit) | Internal | Life of tenant | No |
| Organization membership | Product, directory or HR (per membership) | Internal | Life of tenant, history kept | Yes |
| Directory mapping and sync run log | Product | Internal | 365 days for run logs | No |

When a directory or HR source owns a unit or membership, its source fields are read-only in the product UI (source-of-truth rule from foundation section 9.5).

## 9. Package entitlements
| Capability key | Behavior |
|---|---|
| `org.hierarchy` | All packages |
| `org.delegated_admin` | Workplace Plus and above (organization administrators). Workplace: tenant admins only. |
| `org.max_units` | Quota per package. Values are commercial data, set in the package definition (open commercial item C3). |
| `org.directory_sync` | All packages for one directory connection. Workplace Plus and above for more than one connection. |

## 10. UX, localization and accessibility
- Tree view with keyboard navigation (arrow keys, expand/collapse) following the WAI-ARIA tree pattern.
- Mirrored tree indentation in RTL.
- Move action with confirmation showing affected unit count.
- Directory-owned units and memberships show a "Managed by Active Directory" badge and a link to the mapping page.
- Mapping page: strategy picker, preview diff grouped by create, rename, move and deactivate, unmapped values and users, last run status.

## 11. AI behavior
Not applicable.

## 12. Integrations and events
Publishes `OrganizationUnitCreated`, `OrganizationUnitMoved`, `OrganizationUnitDeactivated`, `OrganizationMembershipChanged`, `DirectorySyncCompleted`.

Directory reads use the connection from spec 003: Microsoft Graph for Entra ID, and LDAPS through the connector framework (spec 017) for on-premises AD. For a TeamBeats SaaS tenant, on-premises AD is reached through an outbound sync agent installed in the customer network; for an on-premises TeamBeats install, the server may connect to LDAPS directly.

## 13. Audit

| Event | Type | Key fields |
|---|---|---|
| org.unit_created / renamed / moved / deactivated | audit | unit, old and new values, actor |
| org.membership_added / ended / primary_changed | audit | user ref, unit, actor (user or directory connection) |
| org.directory_mapping_changed | audit | strategy, base DN or attributes, actor |
| org.directory_sync_completed / halted | operational and audit | connection, counts, threshold breach if any |

## 14. Security and abuse cases
- Org admin escalates by moving a unit they don't own into their subtree: denied (both source and destination must be in scope).
- Bulk import file with units for another tenant's codes: rows are always created in the importer's tenant; cross-tenant parent codes fail validation.
- Compromised or misconfigured directory empties an OU: the deletion threshold stops the run (FR-013).
- A user edits their own `department` attribute in AD to gain access to another unit: the product trusts only the configured directory, so this is a directory permissions issue; the change is audited with the sync run so it can be traced.

## 15. Non-functional requirements
- Ancestor and descendant queries p95 under 50 ms at design-to scale.
- Subtree move of 1,000 units completes under 5 seconds.
- First full directory sync of 20,000 users and 2,000 units completes under 30 minutes; an incremental run with fewer than 500 changes completes under 2 minutes.

## 16. Success criteria
- **SC-001**: A tenant admin can model a three-level group structure of 50 units in under 30 minutes.
- **SC-002**: No access granted by structure alone (verified by tests in spec 004).
- **SC-003**: A tenant admin with an Entra ID tenant can map their AD structure and see it in TeamBeats in under one hour, with no CSV.

## 17. Open questions
- The Active Directory mapping (US5, FR-010 to FR-014) needs Mohamed's approval before implementation. Defaults used: 30 minute schedule, 10% deletion threshold, one strategy per connection.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| TEN-003 | FR-001, US1 |
| TEN-004 | FR-002 |
| TEN-005 | FR-003, US1 scenarios 2 and 3 |
| TEN-006 | FR-005, US3 |
| TEN-010 | FR-008 |
| INT-007 (directory part) | FR-010 to FR-014, US5 |
