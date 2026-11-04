# Feature Specification: Organization Hierarchy and Memberships

| Field | Value |
|---|---|
| Spec ID | 002-organization-hierarchy |
| Area | platform |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R0 |
| Last updated | 2026-11-04 |
| Approved | 2026-11-04 by Mohamed |
| Requirement IDs | TEN-003, TEN-004, TEN-005, TEN-006, TEN-010 |
| Use cases | UC-02 |
| Depends on | 001, 004 (scoped admin roles), 006 (audit) |

## 1. Problem and outcome

**Problem:** Customers are groups of companies, not single companies. A holding company may have subsidiaries that must be run separately but sit under one contract. Modelling each subsidiary as a separate tenant breaks shared services; modelling them as one flat tenant leaks data between them.

**Outcome:** Each tenant has a hierarchy of organization units (parent organizations, subsidiaries, divisions, departments, business units). Users can belong to one or more units. The hierarchy is available to navigation, targeting, authorization and reporting.

## 2. Scope

**In scope:**
- Create, rename, move, deactivate organization units.
- Built-in unit types plus tenant-defined types.
- Organization memberships (a user in one or more units, with a membership type).
- Preferred (home) organization per user.
- Cycle prevention and cross-tenant reference prevention.
- Bulk import from a file. (Decided 2026-11-04: CSV import is delivered in R1, not R0.)

**Out of scope:**
- HR-driven synchronization (UC-09, integration spec).
- Permission inheritance rules (spec 004 owns these; this spec only provides the structure).
- Org chart of people by reporting line (directory spec). Reporting line is not the same as organization unit membership.

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

### Edge cases
- Depth limit. (Decided 2026-11-04: maximum 12 levels.)
- Duplicate codes within a tenant: rejected. Same code in different tenants: allowed.
- Renaming a unit keeps its ID and code; localized names supported per enabled locale.
- External identifiers (from HR) are unique per tenant and source system.

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

## 6. Business and validation rules
- Exactly one primary membership per user per tenant when the user has any memberships.
- A unit's type does not restrict where it can sit in the tree. (Decided 2026-11-04: no ordering rule; the UI shows a warning for unusual placements.)

## 7. Tenant and organization scope
- All entities tenant-owned with RLS.
- Cross-tenant denial: read unit, set parent, add membership, list tree.
- Org boundary: org admin of A cannot read or change sibling B's units or memberships unless granted.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Organization unit | Product, or HR when sync is enabled | Internal | Life of tenant | No |
| Organization membership | Product, or HR when sync is enabled | Internal | Life of tenant, history kept | Yes |

When HR sync is enabled, HR-owned fields become read-only in the product UI (source-of-truth rule from foundation section 9.5).

## 9. Package entitlements
| Capability key | Behavior |
|---|---|
| `org.hierarchy` | All packages |
| `org.delegated_admin` | Workplace Plus and above (organization administrators). Workplace: tenant admins only. |
| `org.max_units` | Quota per package. Values are commercial data, set in the package definition (open commercial item C3). |

## 10. UX, localization and accessibility
- Tree view with keyboard navigation (arrow keys, expand/collapse) following the WAI-ARIA tree pattern.
- Mirrored tree indentation in RTL.
- Move action with confirmation showing affected unit count.

## 11. AI behavior
Not applicable.

## 12. Integrations and events
Publishes `OrganizationUnitCreated`, `OrganizationUnitMoved`, `OrganizationUnitDeactivated`, `OrganizationMembershipChanged`.

## 13. Audit

| Event | Type | Key fields |
|---|---|---|
| org.unit_created / renamed / moved / deactivated | audit | unit, old and new values, actor |
| org.membership_added / ended / primary_changed | audit | user ref, unit, actor |

## 14. Security and abuse cases
- Org admin escalates by moving a unit they don't own into their subtree: denied (both source and destination must be in scope).
- Bulk import file with units for another tenant's codes: rows are always created in the importer's tenant; cross-tenant parent codes fail validation.

## 15. Non-functional requirements
- Ancestor and descendant queries p95 under 50 ms at design-to scale.
- Subtree move of 1,000 units completes under 5 seconds.

## 16. Success criteria
- **SC-001**: A tenant admin can model a three-level group structure of 50 units in under 30 minutes.
- **SC-002**: No access granted by structure alone (verified by tests in spec 004).

## 17. Open questions
- None blocking. All items Decided 2026-11-04.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| TEN-003 | FR-001, US1 |
| TEN-004 | FR-002 |
| TEN-005 | FR-003, US1 scenarios 2 and 3 |
| TEN-006 | FR-005, US3 |
| TEN-010 | FR-008 |
