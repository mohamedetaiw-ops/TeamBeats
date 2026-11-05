# R1 implementation notes

What was built for R1, slice by slice, and where it differs from the specs and plans. Each difference is either covered by an ADR or listed as a follow-up.

## Entra ID sign-in (spec 003 User Story 2)

### Built
- OIDC authorization code flow with PKCE, written on the existing server-side session model instead of the ASP.NET Core OIDC handler. The handler signs in through its own cookie scheme, and TeamBeats sessions are opaque PostgreSQL rows (ADR-0007), so a small flow in `Identity/Sso` is simpler than adapting the handler per tenant.
- Token validation uses `Microsoft.IdentityModel.Protocols.OpenIdConnect` (MIT, Microsoft): discovery, signing key rotation and JWT signature checks. Writing JWT validation by hand would be a security risk, and the current stack has no library for it.
- Routes on the tenant host: `GET /api/v1/auth/signin/options`, `GET /api/v1/auth/signin/sso`, `GET /api/v1/auth/signin/sso/callback`. Admin API: `GET` and `PUT /api/v1/tenant/identity-provider`, permission `identity.config_manage`, capability `identity.oidc`.
- Issuer pinned to the tenant's own directory (`https://login.microsoftonline.com/{directory}/v2.0`); `common` and `organizations` are refused at save.
- Link key is issuer plus the Entra `oid` claim (FR-003), so directory sync (US6) will meet the same identity.
- Protections: state and browser binding stored only as hashes; the state is consumed on first use (callback, code and token replay); the binding cookie must match (login CSRF); nonce must match; 5 minute clock skew; return paths are same-origin relative only.
- JIT for allowed domains creates an employee membership with no organization memberships. A new identity whose email already belongs to another account is refused, never linked.
- Group-to-role mapping is limited to built-in roles. Roles granted by a mapping are tracked in `identity.group_role_grant` and removed when the user leaves the group. Admin-assigned roles are never removed, and the last tenant administrator is kept. With group overage in the token, mapped roles are left unchanged.
- Web: "Sign in with Microsoft" on the sign-in page when single sign-on is set up, and a problem page for no access, suspended, provider unavailable, not configured and failed, in Arabic and English.

### Differences and follow-ups
| Item | Status |
|---|---|
| Secret manager | No product is chosen yet. `ISecretStore` is the port; `ConfigurationSecretStore` reads `Secrets:tenants:{tenantId}:{name}` from host configuration (Fly.io secrets in staging). References are `secret://tenants/{tenantId}/{name}` and are refused for any other tenant. Open decision: which secret manager (for example Azure Key Vault, OpenBao). |
| Tenant discovery by email domain (T015) | Not built. Sign-in starts on the tenant's own host. Needs an apex host, which the host resolver does not have yet. |
| Identity provider admin screen (T011) | API only. The screen comes with the admin pages of spec 008. |
| Identity provider sign-out | Product sign-out only. Offering Entra sign-out (US3 scenario 1) follows with the shell. |
| Sovereign clouds | Only the commercial Entra authority is accepted. |
| Group overage | Roles stay unchanged; the Graph lookup is R2 as planned. |
