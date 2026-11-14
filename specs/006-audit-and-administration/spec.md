# Feature Specification: Audit Trail and Admin Shell

| Field | Value |
|---|---|
| Spec ID | 006-audit-and-administration |
| Area | platform |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R0 (audit, admin shell), R1 (Connections area with Microsoft 365 connection for directory sync, mail and calendar), R2 (connection features for SharePoint documents and rooms, and other connectors from spec 017; "API and MCP access" area of spec 035) |
| Last updated | 2026-11-08 |
| Approved | 2026-11-04 by Mohamed; Connections area (US5, FR-010 to FR-015) 2026-11-05 by Mohamed |
| Requirement IDs | ADM-001, ADM-002, ADM-003 (retention part), TEN-010, INT-001 (connection administration part), new AUD-001 to AUD-007 |
| Use cases | UC-01, UC-10 and UC-11 (connection setup part), UC-13 (audit part) |
| Depends on | 001, 004, 005, 026 (step-up for connection credentials) |

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
- Connections area: one place where integration administrators create, consent, test, monitor, disable and delete the tenant's connections to external systems, and turn individual features of a connection on or off.
- A place in the admin shell for the "API and MCP access" area of spec 035 (apps, consents, event subscriptions, usage), next to Connections. Connections are TeamBeats calling out; API and MCP access is other systems calling in. Its data lives in the `PublicApi` module.

**Out of scope:**
- SIEM streaming (spec 026, R2). This spec provides the extension point only.
- Product analytics (spec 021).
- Legal hold management (spec 011). This spec only honours holds during purge.
- What each connected feature does with a connection (directory sync in 003, SharePoint documents in 011, mail, calendar and rooms in 018, connector packs in 017 to 019). This spec owns the shared connection model and screens only.

## 3. Actors

| Actor | Can do |
|---|---|
| Tenant administrator | Search and export all tenant audit events; set retention |
| Security officer (role) | Search and export audit; cannot change settings |
| Organization administrator | Search audit events for their scope only |
| Platform operator | See platform-level audit (tenant creation, break-glass) |
| Integration administrator (role) | Manage connections and their features (`integration.manage`); see connection health and runs |

Permission keys (spec 004 FR-013): `audit.read` (search audit, limited to the holder's scope), `audit.export` (export audit results), `audit.retention_manage` (set audit retention). The tenant administrator and security officer roles hold `audit.read` and `audit.export` at tenant scope by default; only the tenant administrator holds `audit.retention_manage`; the organization administrator holds `audit.read` at its own scope.
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
3. Records under legal hold are never purged. A spec 011 legal hold selects audit records by custodian (as actor or target) and date range, and the purge job checks the legal hold contract before deleting. (Decided 2026-11-04: legal hold is delivered in R2, through spec 011; decided 2026-11-05: holds cover audit records.)

### User Story 4: Admin shell (Priority: P1)

**Acceptance scenarios:**
1. **Given** a tenant admin, **When** they open administration, **Then** they see Tenant settings, Organization, Users and groups, Roles, Plan and usage, Identity, Connections, Audit. An integration administrator who is not a tenant admin sees Connections only.
2. **Given** an org admin, **Then** they see only Organization (their scope), Users (their scope), Roles (delegable) and Audit (their scope).
3. **Given** an employee, **When** they open an admin URL directly, **Then** they get a no-access page and the API denies the data.

### User Story 5: Manage connections (Priority: P1, R1)

Acme's integration administrator, Karim, connects Acme's Microsoft 365 tenant once. He turns on directory sync for R1, and later SharePoint documents. Each feature asks only for the Microsoft permissions it needs, and he can see at a glance whether each connection is healthy.

**Acceptance scenarios:**
1. **Given** an integration administrator, **When** they open Connections, **Then** the page shows only the connections already established, for managing them: each with its product mark and type (for example Microsoft 365, AD DS over LDAPS, a REST connector from spec 017), the external tenant or endpoint, the features turned on, status (healthy, degraded, needs consent, credential expiring, disabled), last successful run and the owner, connections that need attention first, and Test, Turn on or off and Manage on each one. No setup form is on this page; an empty tenant sees an empty state that leads to "Add connection".
2. **Given** they choose "Add connection", **Then** a wizard opens with four steps: choose a type, set up, test, turn on. The first step shows every connection type as a tile with the product's mark (generic icon for custom REST, webhooks and file imports), grouped by what it connects, with a search; types outside the package are listed apart and cannot be chosen. **Given** they choose Microsoft 365, **When** they enter the Entra tenant ID and choose the features to turn on, **Then** before consent they see, per feature, the Microsoft permissions it needs, whether each is application or delegated, and why. Features not included in the tenant's package are shown with the package needed and cannot be turned on.
3. **Given** the permissions are shown, **When** they grant admin consent in Microsoft, or register the customer's own app with a certificate or secret stored as a secret manager reference, **Then** TeamBeats records the consent, never shows the secret again, and asks for step-up re-authentication (spec 026) before saving the credential.
4. **Given** a saved connection, **When** they run "test connection", **Then** each turned-on feature is checked separately and the result lists what works, what is missing, and any permissions broader than needed.
5. **Given** a healthy Microsoft 365 connection used for directory sync, **When** they turn on another feature (for example SharePoint documents), **Then** only the added permissions are requested through a new consent; the existing features keep working while consent is pending.
6. **Given** consent is revoked in Microsoft, a credential expires or the external system fails health checks, **Then** the connection shows the matching status, the integration administrator gets an alert (credential expiry also 30, 14 and 3 days before), and features using it go to their degraded state (constitution XVII).
7. **Given** they disable a connection or a single feature, **Then** dependent features stop calling the external system within 1 minute and show their degraded or unavailable state; enabling it again resumes them without new setup.
8. **Given** they delete a connection, **Then** they first see what depends on it (for example 12 connected libraries, directory sync of 3,400 users), must confirm, and the owning features remove their derived data as their specs define. Deleting a directory connection does not delete users; spec 003 rules apply.
9. **Given** an organization administrator or an employee, **When** they call the connections API, **Then** it is denied.

## 5. Functional requirements

- **AUD-001 / FR-001**: The platform MUST provide a single audit write contract used by all modules. *(ADM-002)*
- **AUD-002 / FR-002**: Audit storage MUST be append-only for the application role.
- **AUD-003 / FR-003**: Each tenant's audit stream MUST be hash-chained so tampering is detectable.
- **AUD-004 / FR-004**: Audit search MUST respect tenant and organization scope.
- **AUD-005 / FR-005**: Audit MUST NOT contain secrets, tokens or full content bodies. Personal data in audit is limited to identifiers needed for accountability.
- **AUD-006 / FR-006**: Retention MUST be configurable per tenant above a platform minimum. *(ADM-003)*
- **AUD-007 / FR-007**: Audit export MUST be asynchronous, tenant-scoped and itself audited.
- **FR-008**: The admin shell MUST show only areas the user is authorized for, with every area enforced by the backend. Every page MUST offer people with at least one area a way into the admin shell, the Administration row of the account menu (spec 008 FR-062). *(ADM-001)*
- **FR-009**: User administration MUST allow viewing a user's memberships, roles and sign-in status, and disabling a tenant membership.
- **FR-010**: The admin shell MUST provide a Connections area listing every connection of the tenant with type, external tenant or endpoint, features, status, last successful run and owner, available to holders of `integration.manage`. The area's main page is for managing established connections only (status, test, turn on or off, open to edit or delete); adding a connection MUST NOT be a form on that page. *(INT-001)*
- **FR-016**: Adding a connection MUST be a wizard with the steps choose a type, set up, test and turn on, each step with its own address so Back, reload and the Microsoft consent return keep the place. Step one MUST show each connection type with the product's mark from spec 036 DLS-076, grouped, searchable by name, with types outside the tenant's package shown apart and not selectable. Saving the setup creates the connection turned off; the test step MUST let the administrator test and fix a missing allowed host in place; moving on does not require a passing test, and the connection stays off until the administrator turns it on. The API checks every rule again at each step. *(INT-001)*
- **FR-011**: A connection MUST be one tenant-owned record per external system instance (for example one Microsoft 365 connection per Entra tenant) with a set of features that are turned on separately. Each feature MUST declare the external permissions it needs, and the screen MUST show them before consent. Features MUST be gated by the capability keys of their own specs (for example `integration.microsoft365` in spec 005). *(INT-001, INT-002)*
- **FR-012**: Credentials MUST be stored as secret manager references, never shown after saving, and saving or replacing one MUST require step-up re-authentication (spec 026). Admin consent and the permissions granted MUST be recorded.
- **FR-013**: Test connection MUST check each turned-on feature separately and MUST warn when granted permissions are broader than the features need.
- **FR-014**: Connection status MUST be one of healthy, degraded, needs consent, credential expiring, disabled. Status changes and credential expiry MUST alert integration administrators, and features using the connection MUST show their degraded state (constitution XVII).
- **FR-015**: Disabling a connection or a feature MUST stop external calls within 1 minute. Deleting a connection MUST show its dependents and require confirmation; each owning spec defines what happens to its derived data.
- **FR-017**: The admin navigation MUST group its sections under labelled headings (Workspace, People and access, HR, Content and experience, Security and compliance, AI and integrations), in that order, each group shown only when the viewer has at least one of its sections; inside a group sections are sorted by their localized names. Acceptance: an administrator with every section sees six groups; a person with one section sees one group.
- **FR-018**: The admin landing page MUST be an overview, not an empty "Choose a section" screen: setup status (the tenant's setup steps and which are done, for example identity provider, organization, branding, connections), the most recent audited admin changes the viewer may see (spec 006 audit), and shortcuts to the viewer's most used sections. Recent changes MUST read as words (the action's own label, or the change's area when it has none), with the action key only as secondary text. Until TeamBeats records which sections each person uses, shortcuts are at most six tiles: sections with items waiting first, then setup steps still to do, then the first section of each group; the page MUST NOT repeat the full admin menu, which stays beside it. Each block is a shared widget (spec 036 DLS-089). Each block reads through the owning module's contract and the viewer's own permissions. Acceptance: the landing shows setup status and recent changes in words for an administrator, at most six shortcuts, and nothing the viewer may not open.
- **FR-019**: Admin tables MUST follow DLS-079: identifiers such as email addresses are never clipped, and on narrow widths secondary columns hide by priority and the table scrolls inside its container. Acceptance: at 900px the admin People table shows every email in full.

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
| Connection (type, external tenant or endpoint, features, granted permissions, secret reference, status) | Product (secret in secret manager); owned by `Integrations` (including the Microsoft 365 connection), AD DS directory connections and sync state by `Identity` | Confidential | Until deleted, then 90 days of status history |

## 9. Package entitlements
Audit, search, export, the admin shell and the Connections area are part of every package and have no capability key (constitution VI). SIEM streaming is gated by `security.siem` in spec 026. Each connection feature is gated by its own key (for example `identity.directory_sync` in spec 003, `integration.microsoft365` in spec 005).

## 10. UX
- Audit table with filters, localized action names, timestamps shown in the user's time zone, RTL-aware.
- Export button with job status.

## 11. AI behavior
Not applicable. AI and agent events are written through the same contract by specs 013 and 027.

## 12. Integrations and events
Publishes `AuditIntegrityCheckFailed` and `AuditExportCompleted`. Exposes a read-only event feed contract that spec 026 uses for SIEM streaming.

The Connections area is a screen in the `Administration` module, which owns no data. Connection records, consent, credentials, health checks and the adapters for each external system live in `Integrations` (planned spec 017); the Microsoft 365 connection, including its directory sync feature, lives there too. AD DS (LDAPS) directory connections and directory sync state of spec 003 live in `Identity` and appear in the same list. Both expose the connection list and actions through their `Contracts` projects (ADR-0013). `Integrations` publishes `ConnectionCreated`, `ConnectionFeatureChanged`, `ConnectionConsentRevoked`, `ConnectorUnavailable`, `ConnectorRestored` and `ConnectionDeleted`; features consume them to switch to and from their degraded state.

## 13. Audit
The audit service audits its own sensitive actions: export, retention change, purge, integrity check failure.

| Event | Key fields |
|---|---|
| integration.connection_created / deleted | connection, type, external tenant, actor |
| integration.feature_enabled / disabled | connection, feature, actor |
| integration.consent_granted / revoked_detected | connection, feature, permissions granted |
| integration.credential_saved / replaced | connection, secret reference ID (no value), actor |
| integration.status_changed | connection, from, to |

## 14. Security
- Audit search is a data exfiltration path: rate-limited, scoped, and export requires `audit.export`, checked by the central authorization service like every other permission.
- Integrity failures raise an alert to platform operations.
- Connections are a high-value target: management needs `integration.manage`, credential changes need step-up, and secrets never reach the browser, logs or audit.

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
| INT-001 (connection administration) | FR-010 to FR-016, US5 |
