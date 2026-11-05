# Feature Specification: Identity and Authentication

| Field | Value |
|---|---|
| Spec ID | 003-identity-and-authentication |
| Area | platform |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R0 (dev auth), R1 (Entra ID), R2 (Google, SAML) |
| Last updated | 2026-11-04 |
| Approved | 2026-11-04 by Mohamed |
| Requirement IDs | IAM-001 to IAM-007, IAM-009, IAM-010 |
| Use cases | UC-03 |
| Depends on | 001, 002, 004 |

## 1. Problem and outcome

**Problem:** Enterprise customers will not create separate passwords for an intranet. They expect single sign-on through their own identity provider, with their MFA and conditional access. The team still needs a simple way to sign in during early development.

**Outcome:** Employees sign in through their organization's identity provider (Microsoft Entra ID first). The product maps them to exactly one tenant membership, resolves their organization memberships and roles, and creates a secure session. A development-only sign-in exists for local work and cannot reach production.

## 2. Scope

**In scope:**
- Development-only local sign-in, its isolation and its decommission gate.
- Per-tenant identity provider configuration (OIDC first).
- Microsoft Entra ID via OIDC (R1). Google Workspace via OIDC and SAML 2.0 (R2).
- Mapping external subject to an internal user and tenant membership.
- Just-in-time provisioning when allowed by tenant policy.
- Session creation, refresh, logout and invalidation.
- Tenant discovery at sign-in (which tenant is this user signing into).

**Out of scope:**
- Authorization rules (spec 004).
- SCIM provisioning implementation (extension point only, IAM-010).
- HR-driven joiner/mover/leaver (integration spec).
- Password reset, registration or any local credential store in production.

## 3. Actors

| Actor | Role |
|---|---|
| Employee | Signs in |
| Tenant administrator | Configures the tenant's identity provider, JIT policy, group-to-role mapping |
| External identity provider | Authenticates the user and issues claims |
| Developer / test automation | Uses dev sign-in in Development and Test only |

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
2. **Given** an active session, **When** an admin disables the user's tenant membership, **Then** the session is rejected on the next request after the invalidation window. *(IAM-009)* (Decided 2026-11-04: immediate for disable, at most 5 minutes for role changes.)
3. **Given** the tenant is suspended, **Then** all its sessions are rejected (spec 001).

### User Story 4: Google Workspace and SAML (Priority: P2, R2)

Same behaviors as Story 2 through Google OIDC and through SAML 2.0. *(IAM-005, IAM-006)*

### Edge cases
- Clock skew between product and identity provider: tolerate up to 5 minutes.
- Identity provider down: show a clear error; do not fall back to any local login.
- User changes email at the identity provider: identity is keyed on issuer and subject, not email, so access continues.
- Same human in two tenants: two unrelated user records, one per tenant (decided 2026-11-04).

## 5. Functional requirements

- **FR-001**: The dev sign-in MUST exist only in a separate component referenced by the Development/Test host, with a production startup guard and a CI test. *(IAM-001, IAM-002, IAM-003)*
- **FR-002**: Each tenant MUST have its own identity provider configuration(s). Secrets MUST be stored in the secret manager and referenced, never stored in plain configuration.
- **FR-003**: External identities MUST be keyed by tenant, issuer and subject, not by email. There MUST NOT be a global, cross-tenant user or person record.
- **FR-004**: User identity MUST be separate from tenant membership and organization membership. Signing in proves who you are; membership decides where you can go. *(IAM-007)*
- **FR-005**: The product MUST support Entra ID via OIDC (IAM-004), then Google OIDC (IAM-005) and SAML 2.0 (IAM-006) without changes to authorization.
- **FR-006**: Browser sessions MUST use secure, HTTP-only, same-site cookies. Tokens MUST NOT be stored in browser local storage.
- **FR-007**: Disabling a user or membership MUST invalidate sessions within the configured window. *(IAM-009)*
- **FR-008**: The system MUST expose a provisioning extension point compatible with SCIM 2.0 (create, update, deactivate user, group membership) for later implementation. *(IAM-010)*
- **FR-009**: MFA and conditional access are the identity provider's responsibility. The product MUST NOT downgrade them.
- **FR-010**: All sign-in successes, failures and refusals MUST be audited without storing tokens.

## 6. Business rules
- Tenant discovery: user enters work email or uses the tenant's URL. The email domain maps to a tenant's configured identity provider. Domain-to-tenant mapping must be unique in a shared installation.
- A verified domain can belong to only one tenant per installation.

## 7. Tenant and organization scope
- Identity mapping records are tenant-scoped and protected by RLS like other tenant data. There is no global user table.
- Cross-tenant denial: token from tenant B's issuer used against tenant A; session from tenant A used on tenant B's host.

## 8. Data

| Entity | System of record | Classification | Personal data? |
|---|---|---|---|
| External identity link (issuer, subject) | Identity provider | Confidential | Yes |
| Tenant membership | Product (or SCIM/HR later) | Internal | Yes |
| IdP configuration | Product (secret in secret manager) | Confidential | No |
| Session | Product | Confidential | Yes |

## 9. Package entitlements

| Capability key | Behavior |
|---|---|
| `identity.oidc` | All packages |
| `identity.saml` | All packages (Decided 2026-11-04) |
| `identity.scim` | Workplace Plus and above, once implemented |

## 10. UX, localization and accessibility
- Sign-in page localized, RTL aware, shows tenant branding once the tenant is known.
- Error pages: no access, tenant suspended, identity provider unavailable, misconfigured provider (admin-friendly detail only for admins).

## 11. AI
Not applicable.

## 12. Events
Publishes `UserSignedIn`, `UserProvisioned` (JIT), `SessionRevoked`.

## 13. Audit

| Event | Type | Key fields |
|---|---|---|
| auth.sign_in_succeeded / failed / refused | audit | tenant, issuer, subject hash, reason |
| auth.idp_config_changed | audit | tenant, provider, actor (no secret values) |
| auth.session_revoked | audit | user, reason |

## 14. Security and abuse cases
- Token replay, wrong audience, wrong issuer, expired token, missing nonce: all rejected (tests required).
- Open redirect on return URL: only relative or tenant-registered URLs allowed.
- Login CSRF: state parameter validated.
- Dev sign-in reachable in production: prevented by FR-001 (test required).

## 15. Non-functional requirements
- Sign-in round trip adds no more than 300 ms of product-side processing at p95.

## 16. Success criteria
- **SC-001**: An Acme employee can sign in with Entra ID in under 10 seconds including MFA, on first attempt, after the tenant admin completes setup.
- **SC-002**: Tenant admins can configure Entra ID from documentation alone in under 30 minutes.

## 17. Open questions
1. RESOLVED 2026-11-04 (Mohamed): identities are tenant-scoped. The same issuer and subject in two tenants creates two unrelated users. No global person record exists. Cross-tenant identity linking, if ever needed, requires a new decision and ADR.
2. Decided 2026-11-04: role changes take effect in sessions within 5 minutes.
3. Decided 2026-11-04: SAML is available in all packages.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| IAM-001, IAM-002, IAM-003 | FR-001, US1 |
| IAM-004 | FR-005, US2 |
| IAM-005, IAM-006 | FR-005, US4 |
| IAM-007 | FR-004 |
| IAM-009 | FR-007, US3 |
| IAM-010 | FR-008 |
