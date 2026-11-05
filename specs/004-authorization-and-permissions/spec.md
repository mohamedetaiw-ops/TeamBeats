# Feature Specification: Authorization and Permissions

| Field | Value |
|---|---|
| Spec ID | 004-authorization-and-permissions |
| Area | platform |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R0, R1 (attribute conditions: FR-014 to FR-016, User Story 7) |
| Last updated | 2026-11-05 |
| Approved | 2026-11-04 by Mohamed |
| Requirement IDs | TEN-007, TEN-008, IAM-007, IAM-008, ADM-001 (roles part) |
| Use cases | UC-02, UC-03 |
| Depends on | 001, 002, 003 |

## 1. Problem and outcome

**Problem:** A group of companies in one tenant needs fine control over who can see and do what, across subsidiaries and departments. If permissions are scattered through the code as role checks, every new feature becomes a new leak risk.

**Outcome:** One central authorization service answers "can this user do this action on this resource in this scope?" for every module, combining tenant membership, organization scope, roles, resource permissions and context. Nothing is inherited down or up the hierarchy unless a rule says so.

## 2. Scope

**In scope:**
- Permission catalogue (actions such as view, edit, publish, approve, manage, execute) per resource type.
- Roles as named bundles of permissions. Built-in roles plus tenant-defined roles.
- Role assignments to users or groups at a scope: tenant, organization unit, organization subtree, or a single resource.
- Resource-level permissions (ACL entries) on content, documents, workflows and similar.
- Explicit inheritance rules for organization scope.
- Attribute conditions on role assignments and resource permissions (R1).
- Separation of audience targeting from access.
- Delegated administration with boundaries.
- An "explain" capability: why was access allowed or denied.

**Out of scope:**
- Source-system permissions for connected content (search and connector specs will plug into this service).
- Entitlement checks (spec 005). A request must pass both authorization and entitlement.

## 3. Actors

| Actor | Can do |
|---|---|
| Tenant administrator | Define tenant roles, assign any role at any scope in the tenant |
| Organization administrator | Assign roles within their scope, only roles they are allowed to delegate |
| Content owner | Manage resource permissions on resources they own |
| Employee | Receives permissions; can view own effective roles |

Permission keys (FR-013): `authz.roles_view`, `authz.roles_manage` (define custom roles), `authz.roles_assign` (assign roles within the holder's scope), `authz.explain` (use the access explainer) and `authz.rules_manage` (inheritance rules).

## 4. User scenarios

### User Story 1: Scoped role assignment (Priority: P1)

The tenant admin makes Sara a Content Publisher for the "Acme Gulf" subtree.

**Acceptance scenarios:**
1. **Given** Sara has Publisher scoped to subtree "Acme Gulf", **When** she publishes a page owned by "Acme Gulf Finance" (a descendant), **Then** it is allowed.
2. **Given** the same assignment, **When** she tries to publish a page owned by sibling "Acme Africa", **Then** it is denied.
3. **Given** the same assignment, **When** she tries to publish a page owned by the parent "Acme Holding", **Then** it is denied (no upward inheritance).
4. **Given** an assignment scoped to a single unit (not subtree) on "Acme Gulf", **When** she publishes for "Acme Gulf Finance", **Then** it is denied. *(TEN-008)*

### User Story 2: No implicit inheritance from membership (Priority: P1)

**Acceptance scenarios:**
1. **Given** Omar is a member of "Acme Holding" (the parent), **When** he opens a document restricted to members of "Acme Gulf", **Then** access is denied. Membership in a parent grants nothing in children.
2. **Given** a tenant rule "members of a unit can view resources restricted to its descendants" is explicitly enabled for unit "Acme Holding", **When** Omar opens the same document, **Then** access is allowed and the explanation names that rule.

### User Story 3: Resource-level permissions (Priority: P1)

**Acceptance scenarios:**
1. **Given** a document shared with group "Legal Reviewers" for view, **When** a member of that group opens it, **Then** it is allowed even if they are outside the owning unit.
2. **Given** an explicit deny is not supported, **Then** access is the union of grants. (Decided 2026-11-04: no explicit deny in R0; restrict by narrowing grants.)
3. **Given** a resource permission is removed, **When** the user next requests the resource, **Then** access is denied (no stale cache beyond the configured window).

### User Story 4: Targeting is not access (Priority: P1)

**Acceptance scenarios:**
1. **Given** a news item readable by the whole tenant and targeted to "Acme Gulf", **When** an "Acme Africa" employee searches for it, **Then** they can open it, but it does not appear in their home feed.
2. **Given** a news item restricted to "Acme Gulf", **When** an "Acme Africa" employee follows a direct link, **Then** access is denied without revealing the title.

### User Story 5: Delegated administration boundaries (Priority: P2)

**Acceptance scenarios:**
1. **Given** an org admin for "Acme Gulf", **When** they assign Publisher to a user for "Acme Gulf Finance", **Then** it is allowed.
2. **When** they try to assign Tenant Administrator, or any role outside their delegable set, **Then** it is denied.
3. **When** they try to assign a role scoped to "Acme Africa", **Then** it is denied.
4. An admin cannot grant a permission they do not hold themselves.

### User Story 6: Explain access (Priority: P3)

**Acceptance scenarios:**
1. **Given** a tenant admin, **When** they ask why user X can edit page P, **Then** the system lists the grants that allowed it (role, scope, rule).

### User Story 7: Narrow a grant with attribute conditions (Priority: P2, R1)

The tenant admin gives the Approver role for the "Acme Gulf" subtree only to people with job grade 7 or above, and shares the payroll library with "Finance" only for employees, not contractors.

**Acceptance scenarios:**
1. **Given** Approver for subtree "Acme Gulf" with condition `job_grade >= 7`, **When** a grade 8 manager approves a request in "Acme Gulf Finance", **Then** it is allowed, and the explanation names the role, the scope and the condition.
2. **Given** the same assignment, **When** a grade 5 member of "Acme Gulf" tries to approve, **Then** it is denied, and the explanation says the condition was not met.
3. **Given** a share to group "Finance" with condition `membership_type = employee`, **When** a contractor in "Finance" opens the library, **Then** access is denied with not-found semantics.
4. **Given** a condition on an attribute the user does not have (for example no job grade synced), **Then** the condition is false and the grant does not apply (fail closed).
5. **Given** a user has no grant at all, **When** any condition would be true for them, **Then** access is still denied: conditions never create access.
6. **Given** a user's job grade changes in the directory, **Then** decisions reflect it within the FR-008 window.

## 5. Functional requirements

- **FR-001**: The system MUST provide one authorization service used by all modules, with decisions CanView, CanEdit, CanPublish, CanApprove, CanManage, CanExecute (extensible per resource type).
- **FR-002**: Every decision MUST first verify active tenant membership in the session's tenant.
- **FR-003**: Role assignments MUST be scoped to tenant, organization unit, organization subtree, or resource. *(TEN-007, IAM-008)*
- **FR-004**: Organization access MUST NOT be inherited in any direction unless an explicit subtree scope or tenant inheritance rule grants it. *(TEN-008)*
- **FR-005**: Resource permissions MUST be grantable to users, groups, organization units and organization subtrees.
- **FR-006**: Audience targeting MUST be stored and evaluated separately from access permissions.
- **FR-007**: Delegated admins MUST only assign delegable roles, within their scope, and never beyond their own permissions.
- **FR-008**: Permission changes MUST take effect within 60 seconds for grants and revocations (Decided 2026-11-04).
- **FR-009**: Modules MUST NOT check role names directly. Denials MUST NOT reveal whether a resource exists (return not-found semantics for unreadable resources).
- **FR-010**: Decisions MUST be explainable for administrators.
- **FR-011**: Built-in roles MUST include: Tenant Administrator, Organization Administrator, Content Editor, Content Publisher, Workflow Designer, Approver, Integration Administrator, AI Policy Administrator, Security Officer (spec 006), Employee. Platform Operator exists outside tenant roles. Later specs add these built-in roles, seeded when the spec that defines their permissions is approved, so that tenants without `authz.custom_roles` can still staff each feature: Communications Administrator (009), Records Manager, Knowledge Editor and Brand Manager (011; spec 033 adds the theme permissions so that Brand Manager is also the design administrator), Page Editor and Page Publisher (032), Service Owner (016), Agent Designer (020), Evaluation Reviewer (034), HR Officer and Finance Officer (022), Process Owner and Process Operator (024), Compliance Officer (025, 026).
- **FR-013**: Permissions MUST be named `<area>.<action>` (for example `content.publish`, `doc.contribute`) and listed in section 3 of the spec that owns the resource. Each permission maps to one of the FR-001 decisions for that resource type. The permission catalogue and the capability catalogue of spec 005 MUST be separate: no key string may appear in both, and an architecture test MUST fail if one does.
- **FR-012**: Batch evaluation (filter a list of resources to those the user can view) MUST be supported for search, feeds and AI retrieval.
- **FR-014**: Role assignments and resource permissions MAY carry a condition that must hold for the grant to apply. A condition MUST only narrow its own grant; it MUST NOT create access, and since there is no explicit deny, access remains the union of the grants whose conditions hold. *(constitution III, R1)*
- **FR-015**: Conditions MUST be built only from the attribute catalogue in section 6 and the operators equals, not equals, in list, not in list, greater or equal and less or equal, combined with AND and OR to a depth of two. Conditions are data validated against a schema; free-form expressions or scripts MUST be rejected. A missing attribute MUST evaluate the condition as false. *(constitution III, XXIX, R1)*
- **FR-016**: Explanations (FR-010) MUST show each condition with the attribute values used, and batch evaluation (FR-012) MUST apply conditions within its existing performance targets. *(R1)*

## 6. Business rules
- Groups can come from the identity provider (claims), HR sync, or be product-defined.
- Every tenant member has the Employee role at tenant scope by default. A tenant can remove default Employee access per membership type, for example for contractors (Decided 2026-11-04).
- Guest memberships (spec 026) never receive the default Employee role. A guest gets access only through explicit shares into a guest space.
- Attribute catalogue for conditions (R1). User attributes: membership type (employee, contractor, guest), job title and office (spec 003 attribute mapping), and employment status, job grade, job function, work country and cost centre once a tenant maps them from its directory or HR integration (planned 019). Resource attributes: classification (constitution XXV), owning organization unit. Request attributes: authentication assurance, meaning MFA performed and step-up freshness (spec 026), and network zone, meaning the named IP ranges of the tenant security policy that the request comes from (spec 026). A request from no named range has no network zone, so a condition on it fails closed. An attribute a tenant has not mapped is absent, so conditions on it fail closed. Adding an attribute is a change to this spec.
- Conditions are a security control, not a premium feature: they are available in every package and need no capability key.
- Audience targeting rules (FR-006) may use the same user attributes, but they remain separate records and never grant access.

## 7. Tenant and organization scope
- Cross-tenant: role assigned in tenant A has no effect in tenant B; group IDs from tenant B cannot be referenced in A.
- Org boundary tests: sibling, parent, child, subtree, single-unit for every decision type.

## 8. Data

| Entity | System of record | Classification |
|---|---|---|
| Role, permission set | Product | Internal |
| Role assignment | Product (or IdP group mapping) | Internal |
| Resource permission | Product | Internal |
| Inheritance rule | Product | Internal |
| Grant condition | Product | Internal |

## 9. Package entitlements
| Capability key | Behavior |
|---|---|
| `authz.custom_roles` | Workplace Plus and above; Workplace uses built-in roles only (Decided 2026-11-04) |
| `org.delegated_admin` | Workplace Plus and above |

## 10. UX
- Role assignment screen with scope picker (tenant, unit, subtree).
- User's "my access" page.
- Admin "explain access" view, showing conditions and the attribute values used.
- Condition editor on role assignment and share screens: pick attribute, operator and values from lists; no free text expressions.

## 11. AI
AI retrieval and agent tools MUST call FR-012 batch evaluation before context assembly (constitution VII).

## 12. Events
Publishes `RoleAssignmentChanged`, `ResourcePermissionChanged` so search indexes, caches and AI retrieval can refresh.

## 13. Audit

| Event | Type |
|---|---|
| authz.role_assigned / revoked | audit |
| authz.role_defined / changed | audit |
| authz.resource_permission_changed | audit |
| authz.inheritance_rule_changed | audit |
| authz.condition_changed | audit (on the assignment or permission it narrows) |
| authz.group_created / changed / deleted | audit |
| authz.group_member_added / removed | audit (actor is a user, the identity provider claim mapping, or a directory connection) |

Access denials are telemetry, not audit, unless the resource is classified Confidential or above.

## 14. Security and abuse cases
- Privilege escalation through delegated admin: covered by US5.
- IDOR (guessing resource IDs): not-found semantics, tests required.
- Stale permissions in cache after revocation: bounded by FR-008, test required.
- Attribute spoofing: user attributes come only from the identity provider, directory or HR sync, never from the user's own profile edits or the request.
- Condition that widens access by mistake: impossible by construction (FR-014); covered by a property test that adding any condition never adds an allowed decision.

## 15. Non-functional requirements
- Single decision p95 under 5 ms from warm cache. Batch filter of 1,000 resources p95 under 50 ms.

## 16. Success criteria
- **SC-001**: Zero access granted by hierarchy alone in the boundary test suite.
- **SC-002**: Admins can answer "why can this person see this?" without engineering support.

## 17. Open questions
- None blocking. All items Decided 2026-11-04.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| TEN-007 | FR-003, US1 |
| TEN-008 | FR-004, US1, US2 |
| IAM-007 | FR-002 |
| IAM-008 | FR-003, FR-005 |
| ADM-001 (roles) | FR-011, FR-013, US5 |
| CX-GOV-001 (experience roles) | FR-011 (with spec 032 FR-048) |
| CX-ORG-002 (visual inheritance never grants access) | FR-004, FR-006 (with spec 033 FR-017) |
| Constitution III (attribute conditions) | FR-014 to FR-016, US7 |
