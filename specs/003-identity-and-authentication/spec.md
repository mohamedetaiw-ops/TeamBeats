# Feature Specification: Identity and Authentication

| Field | Value |
|---|---|
| Spec ID | 003-identity-and-authentication |
| Area | platform |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R0 (dev auth), R1 (Entra ID sign-in and Entra ID directory sync), R2 (Google, SAML, AD FS sign-in, on-premises AD sync) |
| Last updated | 2026-11-05 |
| Approved | 2026-11-04 by Mohamed |
| Requirement IDs | IAM-001 to IAM-007, IAM-009, IAM-010, INT-007 (directory part) |
| Use cases | UC-03, UC-09 (directory part) |
| Depends on | 001, 002, 004, 017 (connector framework, for on-premises AD) |

## 1. Problem and outcome

**Problem:** Enterprise customers will not create separate passwords for an intranet. They expect single sign-on through their own identity provider, with their MFA and conditional access. The team still needs a simple way to sign in during early development.

**Outcome:** Employees sign in through their organization's identity provider (Microsoft Entra ID first). The product maps them to exactly one tenant membership, resolves their organization memberships and roles, and creates a secure session. Tenants on Active Directory sign in with their AD account (through Entra ID, or AD FS for on-premises only directories) and have their users and groups kept in step from the directory. A development-only sign-in exists for local work and cannot reach production.

## 2. Scope

**In scope:**
- Development-only local sign-in, its isolation and its decommission gate.
- Per-tenant identity provider configuration (OIDC first).
- Microsoft Entra ID via OIDC (R1). Google Workspace via OIDC and SAML 2.0 (R2).
- On-premises Active Directory sign-in through AD FS, using OIDC or SAML 2.0 (R2). Hybrid AD that is already synced to Entra ID uses the Entra ID path.
- Directory connection per tenant and sync of users, account status and groups: Entra ID through Microsoft Graph (R1), on-premises AD DS over LDAPS (R2). The same connection feeds organization mapping in spec 002.
- Mapping external subject to an internal user and tenant membership.
- Just-in-time provisioning when allowed by tenant policy.
- Session creation, refresh, logout and invalidation.
- Tenant discovery at sign-in (which tenant is this user signing into).

**Out of scope:**
- Authorization rules (spec 004).
- SCIM provisioning implementation (extension point only, IAM-010).
- HR-driven joiner/mover/leaver (spec 019). Directory-driven joiner, mover and leaver is in scope (US6).
- Password reset, registration or any local credential store in production.
- Sign-in by sending an AD username and password to the product for an LDAP bind. The constitution allows local username and password sign-in only in Development and Test, and an LDAP bind form would put AD passwords through the product. AD sign-in is always federated (Entra ID or AD FS).

## 3. Actors

| Actor | Role |
|---|---|
| Employee | Signs in |
| Tenant administrator | Configures the tenant's identity provider, JIT policy, group-to-role mapping |
| External identity provider | Authenticates the user and issues claims |
| Directory (Entra ID or on-premises AD DS) | Source of users, account status, groups and organization attributes |
| Developer / test automation | Uses dev sign-in in Development and Test only |

Permission keys (spec 004 FR-013): `identity.users_view`, `identity.users_manage` and `identity.config_manage` (identity provider, JIT policy, directory connections and group mapping). The built-in tenant administrator role holds all three at tenant scope.

## 4. User scenarios

### User Story 1: Development sign-in (Priority: P1, R0)

A developer runs the product locally and signs in as a seeded test user of a seeded test tenant.

**Acceptance scenarios:**
1. **Given** the Development environment profile, **When** a developer signs in with a seeded test account, **Then** a session is created for that account's tenant.
2. **Given** a production build artifact, **When** it starts with the dev sign-in setting enabled, **Then** startup fails with an explicit error. *(IAM-002)*
3. **Given** the production build artifact, **When** the CI check inspects it, **Then** the dev sign-in component is not present in the production composition. *(IAM-002, IAM-003)*
4. Seeded test accounts use fictional data only.

### User Story 2: Sign in with Microsoft Entra ID (Priority: P1, R1)

An employee of Acme opens the intranet, is redirected to Entra ID, completes MFA, and lands on the home page.

**Acceptance scenarios:**
1. **Given** tenant A configured with Entra ID (issuer, client ID, secret reference), **When** a provisioned employee signs in, **Then** the product validates issuer, audience, signature, nonce and expiry, maps the subject to the user's tenant A membership, loads org memberships, roles and entitlements, and creates a session. *(IAM-004)*
2. **Given** a valid token from an issuer not configured for tenant A, **When** it is presented for tenant A, **Then** sign-in is refused. 
3. **Given** a user whose tenant membership is disabled, **When** they sign in successfully at the identity provider, **Then** the product refuses access with a "no access" page and audits the attempt.
4. **Given** JIT provisioning is enabled with an allowed domain list, **When** an unknown user from an allowed domain signs in, **Then** a tenant membership is created with the default employee role and no organization memberships until assigned.
5. **Given** JIT is disabled, **When** an unknown user signs in, **Then** access is refused.
6. **Given** a group-to-role mapping (Entra group ID to product role at a scope), **When** the user signs in, **Then** mapped roles are applied. Unknown groups are ignored and logged at debug level only.

### User Story 3: Session invalidation (Priority: P1)

**Acceptance scenarios:**
1. **Given** an active session, **When** the user signs out, **Then** the product session ends and the user is offered identity provider sign-out. 
2. **Given** an active session, **When** an admin disables the user's tenant membership, **Then** the session is rejected on the next request after the invalidation window. *(IAM-009)* (Decided 2026-11-05: immediate for disable; role and permission changes take effect within 60 seconds, spec 004 FR-008.)
3. **Given** the tenant is suspended, **Then** all its sessions are rejected (spec 001).

### User Story 4: Google Workspace and SAML (Priority: P2, R2)

Same behaviors as Story 2 through Google OIDC and through SAML 2.0. *(IAM-005, IAM-006)*

### User Story 5: Sign in with on-premises Active Directory through AD FS (Priority: P2, R2)

A government tenant has AD DS and AD FS but no Entra ID. Employees sign in with their domain account on the AD FS page, using the customer's own MFA, and land in TeamBeats.

**Acceptance scenarios:**
1. **Given** tenant A configured with an AD FS application group (OIDC) or relying party trust (SAML), **When** a synced employee signs in, **Then** the product validates the token as in US2 and links the identity on issuer plus the AD `objectGUID` claim, so it matches the user already created by directory sync.
2. **Given** an AD FS token without the `objectGUID` claim, **When** it is presented, **Then** sign-in is refused and the admin sees which claim rule is missing.
3. **Given** a TeamBeats on-premises install with no internet access, **When** the tenant uses AD FS, **Then** sign-in works with no call outside the customer network.

### User Story 6: Keep users and groups in step with Active Directory (Priority: P1, R1 for Entra ID, R2 for on-premises AD)

The tenant admin connects Acme's directory once. New hires can sign in on day one with the right unit and roles, and leavers lose access as soon as they are disabled in AD.

**Independent test:** connect a test directory; create, rename, disable and re-enable a user and change group membership in the directory; confirm each change after the next sync, including session revocation on disable.

**Acceptance scenarios:**
1. **Given** a tenant admin, **When** they create a directory connection, **Then** they provide either an Entra ID tenant ID with an app registration (client secret or certificate as a secret-manager reference), or an AD DS LDAPS endpoint, base DN and read-only service account (secret-manager reference). A "test connection" action reports reachable, authenticated and counts found, without storing directory data.
2. **Given** a user scope (all users, selected OUs, or members of selected groups), **When** the first sync runs, **Then** each in-scope account gets a user record and an active tenant membership with `created_via = directory`, keyed by the directory's immutable ID (Entra `oid`, AD `objectGUID`), never by email or UPN.
3. **Given** a synced user who signs in for the first time, **When** the token arrives, **Then** it links to the existing user through the immutable ID and no duplicate is created, even if JIT is enabled.
4. **Given** a user disabled or deleted in the directory, or moved out of the sync scope, **When** the next sync runs, **Then** the tenant membership is disabled, sessions are revoked as in US3, and `UserDeprovisioned` is published. Data is kept; nothing is hard-deleted.
5. **Given** a disabled user re-enabled in the directory, **When** the next sync runs, **Then** the same user record is re-enabled with its history and content ownership intact.
6. **Given** selected directory groups, **When** sync runs, **Then** they appear as product groups with source `directory`, members are kept in step, and group-to-role mappings (US2 scenario 6) apply to them. Product groups are never changed by the sync.
7. **Given** attribute mapping (display name, email, job title, phone, office, employee ID, manager, preferred language), **When** sync runs, **Then** the values are passed to the profile (spec 010) as directory-owned fields, and the `manager` link is passed as the reporting line. Unmapped attributes are not read.
8. **Given** the directory is unreachable, **When** a sync runs, **Then** nothing is changed, the run is marked failed with the reason, and the admin is alerted after 3 consecutive failures. Existing users keep signing in through their identity provider.

### Edge cases
- Clock skew between product and identity provider: tolerate up to 5 minutes.
- Identity provider down: show a clear error; do not fall back to any local login.
- User changes email at the identity provider: identity is keyed on issuer and subject, not email, so access continues.
- Entra ID `sub` is different for each app registration, while directory sync sees the `oid`. For Entra ID the link key is issuer plus `oid` so synced and signed-in identities meet. For AD FS it is issuer plus `objectGUID`.
- UPN or email changes in AD: the immutable ID does not change, so the user record updates in place.
- Same account or same human in two TeamBeats tenants (for example two subsidiaries contracted separately): two unrelated user records, one per tenant (decided 2026-11-04, ADR-0005).

## 5. Functional requirements

- **FR-001**: The dev sign-in MUST exist only in a separate component referenced by the Development/Test host, with a production startup guard and a CI test. *(IAM-001, IAM-002, IAM-003)*
- **FR-002**: Each tenant MUST have its own identity provider configuration(s). Secrets MUST be stored in the secret manager and referenced, never stored in plain configuration.
- **FR-003**: External identities MUST be keyed by tenant, issuer and an immutable subject, not by email or UPN. For Entra ID the subject is the `oid` claim; for AD FS it is `objectGUID`; for other providers it is `sub`. There MUST NOT be a global, cross-tenant user or person record.
- **FR-004**: User identity MUST be separate from tenant membership and organization membership. Signing in proves who you are; membership decides where you can go. *(IAM-007)*
- **FR-005**: The product MUST support Entra ID via OIDC (IAM-004), then Google OIDC (IAM-005) and SAML 2.0 (IAM-006) without changes to authorization.
- **FR-006**: Browser sessions MUST use secure, HTTP-only, same-site cookies. Tokens MUST NOT be stored in browser local storage.
- **FR-007**: Disabling a user or membership MUST invalidate sessions within the configured window. *(IAM-009)*
- **FR-008**: The system MUST expose a provisioning extension point compatible with SCIM 2.0 (create, update, deactivate user, group membership) for later implementation. *(IAM-010)*
- **FR-009**: MFA and conditional access are the identity provider's responsibility. The product MUST NOT downgrade them.
- **FR-010**: All sign-in successes, failures and refusals MUST be audited without storing tokens.
- **FR-011**: Each tenant MAY have one or more directory connections (Entra ID through Microsoft Graph, AD DS over LDAPS). Credentials MUST be secret-manager references. Plain LDAP without TLS MUST be refused.
- **FR-012**: Directory sync MUST create, update, disable and re-enable users and tenant memberships, and keep selected groups in step, keyed by the directory's immutable ID. It MUST be incremental after the first run, idempotent and replayable.
- **FR-013**: Disabling or removing an account in the directory MUST disable the tenant membership and revoke sessions at the next sync. A user disabled by the directory MUST NOT be re-enabled by a product admin while the directory still has the account disabled.
- **FR-014**: Directory-owned user fields and groups MUST be read-only in the product. Product-owned fields and groups MUST never be changed by sync.
- **FR-015**: The product MUST NOT accept AD passwords. Active Directory sign-in MUST go through a federated provider (Entra ID or AD FS).

## 6. Business rules
- Tenant discovery: user enters work email or uses the tenant's URL. The email domain maps to a tenant's configured identity provider. Domain-to-tenant mapping must be unique in a shared installation.
- A verified domain can belong to only one tenant per installation.
- Users created by directory sync take priority over JIT. With a directory connection active, JIT is off by default, because a user missing from the sync scope is usually someone who should not have access.
- Default sync schedule: every 30 minutes, with "sync now" for tenant admins. A deletion threshold (default 10% of users disabled in one run) stops the run and alerts the admin, as in spec 002.

## 7. Tenant and organization scope
- Identity mapping records are tenant-scoped and protected by RLS like other tenant data. There is no global user table.
- Cross-tenant denial: token from tenant B's issuer used against tenant A; session from tenant A used on tenant B's host.
- Directory connections, sync runs and synced records are tenant-owned. A sync job runs inside its tenant's context and cannot write to another tenant.

## 8. Data

| Entity | System of record | Classification | Personal data? |
|---|---|---|---|
| External identity link (issuer, subject) | Identity provider | Confidential | Yes |
| Tenant membership | Product (or SCIM/HR later) | Internal | Yes |
| IdP configuration | Product (secret in secret manager) | Confidential | No |
| Session | Product | Confidential | Yes |
| Directory connection (endpoint, scope, secret reference) | Product (secret in secret manager) | Confidential | No |
| Directory-owned user fields and groups | Directory | Internal | Yes |
| Sync run log | Product | Internal | No (counts and IDs only) |

## 9. Package entitlements

| Capability key | Behavior |
|---|---|
| `identity.oidc` | All packages |
| `identity.saml` | All packages (Decided 2026-11-04) |
| `identity.scim` | Workplace Plus and above, once implemented |
| `identity.directory_sync` | All packages |
| `identity.max_directory_connections` | Limit: 1 in Workplace; more in Workplace Plus and above (values are commercial data in the package definition) |

## 10. UX, localization and accessibility
- Sign-in page localized, RTL aware, shows tenant branding once the tenant is known.
- Error pages: no access, tenant suspended, identity provider unavailable, misconfigured provider (admin-friendly detail only for admins).
- Directory page for tenant admins: connections, test connection, scope, attribute and group mapping, last runs with counts, "sync now".

## 11. AI
Not applicable.

## 12. Events
Publishes `UserSignedIn`, `UserProvisioned` (JIT or directory), `UserDeprovisioned`, `UserProfileSynced`, `SessionRevoked`.

Entra ID is read through Microsoft Graph with application permissions limited to reading users, groups and administrative units. On-premises AD DS is read over LDAPS through the connector framework (spec 017); a SaaS tenant uses an outbound-only sync agent in the customer network, while an on-premises TeamBeats install may connect directly.

## 13. Audit

| Event | Type | Key fields |
|---|---|---|
| auth.sign_in_succeeded / failed / refused | audit | tenant, issuer, subject hash, reason |
| auth.idp_config_changed | audit | tenant, provider, actor (no secret values) |
| auth.session_revoked | audit | user, reason |
| identity.directory_connection_changed | audit | tenant, kind, scope, actor (no secret values) |
| identity.user_synced / disabled_by_directory / reenabled_by_directory | audit | user ref, connection, changed fields (names only) |

## 14. Security and abuse cases
- Token replay, wrong audience, wrong issuer, expired token, missing nonce: all rejected (tests required).
- Open redirect on return URL: only relative or tenant-registered URLs allowed.
- Login CSRF: state parameter validated.
- Dev sign-in reachable in production: prevented by FR-001 (test required).
- Over-privileged directory account: the product asks only for read permissions and the test connection warns if write permissions are detected.
- Spoofed AD FS or Entra tenant: the issuer must match the tenant's configured connection, and directory IDs from one connection never link to identities from another.

## 15. Non-functional requirements
- Sign-in round trip adds no more than 300 ms of product-side processing at p95.

## 16. Success criteria
- **SC-001**: An Acme employee can sign in with Entra ID in under 10 seconds including MFA, on first attempt, after the tenant admin completes setup.
- **SC-002**: Tenant admins can configure Entra ID from documentation alone in under 30 minutes.
- **SC-003**: A user disabled in Active Directory loses access within 35 minutes (one sync interval plus processing).

## 17. Open questions
1. RESOLVED 2026-11-04 (Mohamed): identities are tenant-scoped. The same issuer and subject in two tenants creates two unrelated users. No global person record exists. Cross-tenant identity linking, if ever needed, requires a new decision and ADR.
2. Decided 2026-11-05: role and permission changes take effect within 60 seconds for every access check (spec 004 FR-008). Session data cached for display only (menus, name, picture) may refresh within 5 minutes and is never used for an access decision.
3. Decided 2026-11-04: SAML is available in all packages.
4. Active Directory sign-in and sync (US5, US6, FR-011 to FR-015) need Mohamed's approval before implementation. Default: AD sign-in is federated only (no LDAP password bind), Entra ID sync in R1, on-premises AD in R2.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| IAM-001, IAM-002, IAM-003 | FR-001, US1 |
| IAM-004 | FR-005, US2 |
| IAM-005, IAM-006 | FR-005, US4 |
| IAM-007 | FR-004 |
| IAM-009 | FR-007, US3 |
| IAM-010 | FR-008 |
| INT-007 (directory part) | FR-011 to FR-014, US6 |
| IAM-006 (AD FS) | US5, FR-015 |
