# Feature Specification: Audit Trail and Admin Shell

| Field | Value |
|---|---|
| Spec ID | 006-audit-and-administration |
| Area | platform |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R0 |
| Last updated | 2026-11-05 |
| Approved | 2026-11-04 by Mohamed |
| Requirement IDs | ADM-001, ADM-002, ADM-003 (retention part), TEN-010, new AUD-001 to AUD-007 |
| Use cases | UC-01, UC-13 (audit part) |
| Depends on | 001, 004 |

## 1. Problem and outcome

**Problem:** Enterprise and government buyers ask "who changed what, when?" in every security review. Every other spec emits audit events, so they need one consistent, tamper-resistant place to go. Admins also need one shell where tenant, org, user, role and entitlement screens live.

**Outcome:** A shared audit service records security and administrative events append-only, per tenant, searchable by authorized admins and exportable. An admin shell provides navigation for tenant and platform administration with scope-aware menus.

## 2. Scope

**In scope:**
- Audit event model, write API for modules, append-only storage.
- Tamper evidence (hash chain per tenant).
- Audit search and filter for tenant admins and security officers.
- Audit export (CSV/JSON) for a date range.
- Retention configuration per tenant.
- Admin shell: navigation frame, scope switcher, landing dashboard with links to settings areas.
- User and group administration screens (list, view, disable membership, view roles).

**Out of scope:**
- SIEM streaming (spec 026, R2). This spec provides the extension point only.
- Product analytics (spec 021).
- Legal hold management (spec 011). This spec only honours holds during purge.

## 3. Actors

| Actor | Can do |
|---|---|
| Tenant administrator | Search and export all tenant audit events; set retention |
| Security officer (role) | Search and export audit; cannot change settings |
| Organization administrator | Search audit events for their scope only |
| Platform operator | See platform-level audit (tenant creation, break-glass) |
| Modules | Write audit events (system actor) |

## 4. User scenarios

### User Story 1: Record audit events (Priority: P1)

**Acceptance scenarios:**
1. **Given** any audited action from specs 001 to 005, **When** it completes, **Then** an audit record exists with tenant, actor, actor type, action, target type and ID, outcome, timestamp (UTC), correlation ID, source IP, and a changed-fields summary without secret values.
2. **Given** the action's database transaction rolls back, **Then** no success audit record exists. [Default: audit written in the same transaction or via the outbox.]
3. **Given** the application database role, **When** it attempts UPDATE or DELETE on audit records, **Then** the database refuses.
4. **Given** a sequence of records for tenant A, **When** any record is altered outside the application, **Then** the integrity check reports the break position.

### User Story 2: Search and export (Priority: P1)

**Acceptance scenarios:**
1. **Given** a tenant admin of A, **When** they filter by actor, action, date range and target, **Then** only tenant A records are returned, newest first, paginated.
2. **Given** an org admin scoped to "Acme Gulf", **When** they search, **Then** only records whose target is in their scope are returned.
3. **Given** an export of up to 1 million records, **Then** it runs as a background job, the file is stored in tenant-scoped storage, and the export itself is audited.

### User Story 3: Retention (Priority: P2)

**Acceptance scenarios:**
1. **Given** retention of 365 days, **When** records pass that age, **Then** they are purged by a scheduled job, and the purge is audited with count and range.
2. Retention cannot be set below the platform minimum. (Decided 2026-11-04: minimum 365 days.)
3. Records under legal hold are never purged. (Decided 2026-11-04: legal hold is delivered in R2, through spec 011.)

### User Story 4: Admin shell (Priority: P1)

**Acceptance scenarios:**
1. **Given** a tenant admin, **When** they open administration, **Then** they see Tenant settings, Organization, Users and groups, Roles, Plan and usage, Identity, Audit.
2. **Given** an org admin, **Then** they see only Organization (their scope), Users (their scope), Roles (delegable) and Audit (their scope).
3. **Given** an employee, **When** they open an admin URL directly, **Then** they get a no-access page and the API denies the data.

## 5. Functional requirements

- **AUD-001 / FR-001**: The platform MUST provide a single audit write contract used by all modules. *(ADM-002)*
- **AUD-002 / FR-002**: Audit storage MUST be append-only for the application role.
- **AUD-003 / FR-003**: Each tenant's audit stream MUST be hash-chained so tampering is detectable.
- **AUD-004 / FR-004**: Audit search MUST respect tenant and organization scope.
- **AUD-005 / FR-005**: Audit MUST NOT contain secrets, tokens or full content bodies. Personal data in audit is limited to identifiers needed for accountability.
- **AUD-006 / FR-006**: Retention MUST be configurable per tenant above a platform minimum. *(ADM-003)*
- **AUD-007 / FR-007**: Audit export MUST be asynchronous, tenant-scoped and itself audited.
- **FR-008**: The admin shell MUST show only areas the user is authorized for, with every area enforced by the backend. *(ADM-001)*
- **FR-009**: User administration MUST allow viewing a user's memberships, roles and sign-in status, and disabling a tenant membership.

## 6. Business and validation rules
- Audit records are written for the outcome of the action (succeeded, failed, refused). A refused action by an unauthenticated caller is written to the platform stream, not to a tenant stream, unless the tenant is known from the host.
- Timestamps are stored in UTC and shown in the viewer's time zone.
- Export date ranges are limited to 1 million records per job; larger ranges are split into several jobs.

## 7. Tenant and organization scope
- Audit records are tenant-owned with RLS. Platform-level records (tenant creation, break-glass) live in a separate platform audit stream.
- Cross-tenant: search, export and integrity check never cross tenants.

## 8. Data

| Entity | System of record | Classification | Retention |
|---|---|---|---|
| Audit record | Product | Confidential | Tenant setting, min 365 days |
| Audit export file | Product (object storage) | Confidential | 7 days then deleted |

## 9. Package entitlements
Audit, search, export and the admin shell are part of every package and have no capability key (constitution VI). SIEM streaming is gated by `security.siem` in spec 026.

## 10. UX
- Audit table with filters, localized action names, timestamps shown in the user's time zone, RTL-aware.
- Export button with job status.

## 11. AI behavior
Not applicable. AI and agent events are written through the same contract by specs 013 and 027.

## 12. Integrations and events
Publishes `AuditIntegrityCheckFailed` and `AuditExportCompleted`. Exposes a read-only event feed contract that spec 026 uses for SIEM streaming.

## 13. Audit
The audit service audits its own sensitive actions: export, retention change, purge, integrity check failure.

## 14. Security
- Audit search is a data exfiltration path: rate-limited, scoped, and export requires the Tenant Admin or Security Officer role.
- Integrity failures raise an alert to platform operations.

## 15. Non-functional requirements
- Audit write adds under 5 ms p95 to the originating operation.
- Search over 10 million records per tenant p95 under 2 seconds with indexed filters.

## 16. Success criteria
- **SC-001**: A tenant admin can produce a list of all role changes in the last 90 days in under 2 minutes.
- **SC-002**: Every audited event listed in specs 001 to 005 has a passing test asserting it is recorded.

## 17. Open questions
- Retention minimum and legal hold timing: Decided 2026-11-04.
- Decided 2026-11-04: SIEM export comes in a later release, as JSON over HTTPS or syslog. It is specified in spec 026 (R2).

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| ADM-001 | FR-008, FR-009, US4 |
| ADM-002 | AUD-001 to AUD-005 |
| ADM-003 (retention) | AUD-006 |
| TEN-010 | AUD-001 |
