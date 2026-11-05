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

## Connections area with the Microsoft 365 connection (spec 006 User Story 5)

### Built
- New `Integrations` module (module map) with `Integrations.Contracts`: `integrations.connection`, `connection_feature` and `consent_request` tables with RLS.
- Microsoft 365 features in R1: directory sync (application permissions `User.Read.All`, `Group.Read.All`, `AdministrativeUnit.Read.All`, capability `identity.directory_sync`), mail, calendar and free and busy (delegated permissions only, capability `integration.microsoft365`). The catalogue endpoint shows each permission, its type and the reason before consent.
- Admin consent per feature with the TeamBeats multi-tenant application. In a shared installation, Microsoft returns to one platform-host redirect URI, which only routes the browser back to the tenant host named in the state; the tenant host checks the signed-in administrator, `integration.manage`, the single-use state, the browser binding and that Microsoft answered for the connected directory.
- Test connection per feature: token and granted application permissions, user and group counts (nothing stored but counts), warnings for permissions broader than needed, and a failure when the application holds application mail or calendar permissions (spec 018 FR-003). Status: healthy, degraded, needs consent, disabled. Revoked consent is detected and audited.
- Disable or enable a connection or a single feature, delete with a dependents preview and confirmation, audit for every change, events `ConnectionCreated`, `ConnectionFeatureChanged`, `ConnectorUnavailable`, `ConnectorRestored`, `ConnectionDeleted`.
- `IConnectionFeatures` for feature owners (directory sync next) and `IConnectionDependents` so owners can describe what a delete affects.
- Connections entry in the admin navigation for holders of `integration.manage`.
- `identity.directory_sync` added to the capability catalogue and every existing package version (spec 003 section 9: all packages).

### Differences and follow-ups
| Item | Status |
|---|---|
| Customer's own app registration with a secret (US5 scenario 3) | Not built. It needs step-up re-authentication before saving a credential (FR-012, spec 026), which arrives with spec 026. Only the TeamBeats application with admin consent is offered, so no customer secret is stored. |
| Alerts to integration administrators on status change | Status changes are audited and published as events; notifications arrive with the notifications spec. |
| Scheduled health checks | Health is checked by "test connection" and, from the directory sync slice, by each sync run. |
| Connections screen | API and navigation entry only; the screen comes with the admin pages of spec 008. |
| TeamBeats application credentials | Platform configuration `Integrations:Microsoft365:ClientId` and `ClientSecret`, from the platform's secret store. |
