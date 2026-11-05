# Feature Specification: Tenant Management

| Field | Value |
|---|---|
| Spec ID | 001-tenant-management |
| Area | platform |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R0 |
| Last updated | 2026-11-05 |
| Approved | 2026-11-04 by Mohamed |
| Requirement IDs | TEN-001, TEN-002, TEN-009, TEN-010, ADM-001 (tenant part) |
| Use cases | UC-01 |
| Depends on | 003 (initial admin sign-in), 005 (package assignment), 006 (audit) |

## 1. Problem and outcome

**Problem:** Every capability in the product depends on knowing which customer a request belongs to. Without a reliable tenant record and lifecycle, nothing else can be isolated, entitled or audited.

**Outcome:** A platform administrator can create a customer tenant, assign it a package, set its locales and branding, and appoint its first tenant administrator. The tenant is isolated from all other tenants from the moment it exists.

## 2. Scope

**In scope:**
- Create, view, update, suspend, reactivate and close a tenant.
- Tenant key (human-readable, unique) and tenant ID (immutable, system-generated).
- Default and enabled locales, time zone, branding (name, logo, colors).
- Assigning the initial package (through spec 005).
- Appointing one or more initial tenant administrators.
- Tenant status model and its effect on sign-in.

**Out of scope:**
- Self-service sign-up and billing.
- Tenant data export and deletion execution (UC-14). Legal hold is in spec 011 and the deletion certificate in spec 026 FR-019. This spec only defines the Closed status.
- Provisioning separate infrastructure for dedicated deployments (deployment tooling, not product behavior).
- Organization hierarchy (spec 002).

## 3. Actors and permissions

| Actor | What they can do | Scope required |
|---|---|---|
| Platform administrator | Create, suspend, reactivate, close tenants; assign package; appoint first tenant admin | Platform operator role (not a tenant role) |
| Tenant administrator | Update branding, locales, time zone of their own tenant; appoint further tenant admins | Tenant admin role in that tenant |
| Employee | No access to tenant settings | n/a |

Platform administrators do not automatically get access to tenant content. Seeing tenant content requires the audited break-glass path (constitution principle I).

Permission keys (spec 004 FR-013): `tenant.settings_view`, `tenant.settings_manage` and `tenant.admins_manage`. The built-in tenant administrator role holds all three at tenant scope.

## 4. User scenarios

### User Story 1: Create a tenant (Priority: P1)

A platform administrator creates a tenant for a newly signed customer, chooses a package and default locale, and appoints the customer's IT lead as tenant administrator.

**Why this priority:** nothing else can be tested without a tenant.

**Independent test:** create a tenant through the admin API and confirm the appointed administrator can sign in and sees only that tenant.

**Acceptance scenarios:**
1. **Given** a platform administrator, **When** they create a tenant with key `acme`, package `workplace`, default locale `ar`, and admin `it.lead@acme.example`, **Then** the tenant is created with status Active, a new immutable tenant ID, default roles seeded, the package assigned, and an audit event recorded.
2. **Given** a tenant with key `acme` exists, **When** another tenant is created with key `ACME`, **Then** creation fails with a duplicate key error (keys are case-insensitive).
3. **Given** a package key that does not exist, **When** a tenant is created with it, **Then** creation fails and no tenant record remains.
4. **Given** a tenant administrator of tenant A, **When** they call the tenant creation endpoint, **Then** the request is denied.

### User Story 2: Configure tenant branding and locales (Priority: P2)

A tenant administrator sets the company logo, primary color, enabled languages and time zone.

**Acceptance scenarios:**
1. **Given** a tenant admin of tenant A, **When** they enable `ar` and `en` and set `ar` as default, **Then** employees of tenant A see Arabic by default with right-to-left layout and can switch to English.
2. **Given** a tenant admin of tenant A, **When** they try to disable the current default locale, **Then** the change is rejected with a clear message.
3. **Given** a tenant admin of tenant A, **When** they request or update the settings of tenant B by changing the tenant ID in the request, **Then** the request is denied and no data from tenant B is returned.
4. **Given** a logo upload, **When** the file is not an allowed image type or exceeds the size limit, **Then** it is rejected. (Decided 2026-11-04: PNG, JPEG and sanitized SVG, max 1 MB.)

### User Story 3: Suspend and reactivate a tenant (Priority: P2)

A platform administrator suspends a tenant for non-payment or security reasons and later reactivates it.

**Acceptance scenarios:**
1. **Given** an active tenant with signed-in users, **When** it is suspended, **Then** new sign-ins are refused with a "tenant suspended" message, existing sessions are invalidated within the configured session invalidation window, scheduled jobs for that tenant stop, and data is retained unchanged.
2. **Given** a suspended tenant, **When** it is reactivated, **Then** users can sign in again and paused jobs resume.
3. **Given** a suspended tenant, **When** a background message for that tenant is processed, **Then** it is deferred or dropped according to job policy and never processed as active.

### Edge cases
- Tenant key uses characters outside the allowed set (lowercase letters, digits, hyphen, 3 to 40 characters): rejected.
- Initial tenant admin identity is not yet resolvable because identity federation is not configured: admin is created as a pending membership and activated on first sign-in.
- Two platform admins create the same key concurrently: exactly one succeeds.

## 5. Functional requirements

- **FR-001**: The system MUST support many tenants in one installation and one code line. *(TEN-001)*
- **FR-002**: Each tenant MUST have a system-generated immutable tenant ID and a unique, case-insensitive tenant key. *(TEN-002)*
- **FR-003**: Tenant status MUST be one of Provisioning, Active, Suspended, Closed. Only Active tenants allow sign-in.
- **FR-004**: Creating a tenant MUST atomically create the tenant record, seed default roles, assign a package, and create pending admin memberships. Partial creation MUST NOT remain on failure.
- **FR-005**: Tenant settings (branding, locales, time zone, package, AI policy) MUST be stored and returned only within that tenant. *(TEN-009)*
- **FR-006**: The tenant in scope for any request MUST come from the authenticated session, never from client input.
- **FR-007**: Every tenant lifecycle and settings change MUST emit an audit event. *(TEN-010)*
- **FR-008**: Suspending a tenant MUST block sign-in, invalidate sessions, and pause tenant jobs without deleting data.
- **FR-009**: Closing a tenant MUST block all access and mark the tenant for the data export and deletion process. (Decided 2026-11-04: 90 days before deletion, configurable per contract.)
- **FR-010**: Platform operator access to tenant settings MUST NOT grant access to tenant content.

## 6. Business and validation rules
- Tenant key: 3 to 40 chars, `[a-z0-9-]`, not starting or ending with a hyphen, not on a reserved list (`admin`, `api`, `www`, `platform`, ...).
- At least one enabled locale. The default locale must be enabled.
- Enabled locales must be a subset of the product's supported locales (ar, en, fr, ur, hi).
- A tenant must always have at least one active tenant administrator once Active. Removing the last one is rejected.

## 7. Tenant and organization scope
- Tenant-owned data: tenant settings, branding assets, memberships.
- The Tenant record itself is a platform-level table. It is the documented exception to RLS and is only reachable through the platform administration module.
- Cross-tenant denial cases: read settings, update settings, upload branding, list admins for another tenant.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Tenant | Product | Internal | Life of contract + retention period | No |
| Tenant settings and branding | Product | Internal | Same | No |
| Tenant admin appointment | Product | Internal | Same | Yes (reference to user) |

## 9. Package entitlements
Every tenant must have exactly one active package at creation. Package behavior is defined in spec 005.

## 10. UX, localization and accessibility
- Platform admin console: tenant list (search, status filter), tenant detail, create form.
- Tenant admin: settings page with branding preview in both LTR and RTL.
- All labels and errors localized. Branding preview must show contrast warnings if primary color fails WCAG AA against white or dark backgrounds.

## 11. AI behavior and data policy
Not applicable. AI policy settings are stored per tenant here; their content is defined in spec 013 (model policy) and spec 027 (agent policy).

## 12. Integrations and events
- Publishes: `TenantCreated`, `TenantSuspended`, `TenantReactivated`, `TenantClosed`, `TenantSettingsChanged`.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| tenant.created | audit | tenant ID, key, package, actor |
| tenant.status_changed | audit | from, to, reason, actor |
| tenant.settings_changed | audit | changed fields (no secrets), actor |
| tenant.admin_appointed | audit | user ref, actor |

## 14. Security and abuse cases
- Tenant ID tampering in URL or body: denied (test required).
- Enumeration of tenant keys by unauthenticated users: sign-in discovery must not reveal whether a tenant key exists beyond what the identity provider flow requires.
- SVG logo with script content: sanitized or rejected.

## 15. Non-functional requirements
- Tenant creation completes in under 10 seconds excluding identity provider setup.
- Tenant settings reads are cached per tenant with the tenant ID in the cache key.

## 16. Success criteria
- **SC-001**: A new customer tenant can be created and its admin signed in within 15 minutes of the order, without engineering help.
- **SC-002**: Zero cross-tenant results across the full automated negative test suite.

## 17. Open questions
- Decided 2026-11-04: tenants get a subdomain (`acme.teambeats.example`) in shared installations and a single host in dedicated installations.
- Decided 2026-11-04: retention after Closed is 90 days, configurable per contract.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| TEN-001 | FR-001 |
| TEN-002 | FR-002 |
| TEN-009 | FR-005 |
| TEN-010 | FR-007 |
| ADM-001 (tenant branding, locales) | User Story 2 |
