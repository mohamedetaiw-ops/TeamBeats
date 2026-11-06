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

## Entra directory sync (spec 003 User Story 6)

### Built
- Settings per Microsoft 365 connection in `identity.directory_connection`: user scope (all users, or members of selected groups), selected groups, schedule (5 to 1440 minutes, default 30) and deletion threshold (default 10 percent). Admin API: `GET` and `PUT /api/v1/tenant/directory` and `POST /api/v1/tenant/directory/sync`, behind `identity.config_manage` and the capability `identity.directory_sync`.
- `IDirectorySource` (Integrations contract) reads Microsoft Graph `users/delta` and `groups/delta`. The delta links are the cursors, so runs after the first are incremental (FR-012).
- Every account seen is mirrored in `identity.directory_user` and `identity.directory_group`. Each run applies the new changes to the mirror in memory and reconciles the whole mirror, so a run is idempotent and replayable, and a scope change applies without reading the directory again.
- Users are linked by issuer plus Entra `oid` through `identity.directory_user_link` and `identity.external_identity`, so a synced user's first Entra sign-in finds the same account (scenario 3). An existing user who already signed in with the same `oid` is linked, never matched by email. A directory account whose email is already used by another account is skipped and counted in the run log.
- Disable, delete or leaving the scope disables the tenant membership, revokes sessions, publishes `TenantMembershipDisabled` and `UserDeprovisioned`, and sets `disabled_by_directory`. A product admin then gets `conflict.user.disabled_by_directory` on re-enable (FR-013). Re-enable in the directory restores the same user.
- Display name and email are directory-owned (`user_account.owner_fields`) and updated in place; `UserProfileSynced` carries the changed field names for the profile (spec 010). Job title, office, phone, employee ID and preferred language are kept in the mirror for spec 010.
- Selected groups become authz groups with source `idp` and key `entra:{group id}`, members kept exactly in step. The identity provider's group-to-role map is applied to the group itself, so a member who leaves loses the role. A deselected or deleted group is emptied. Product groups are refused by the sync, and product admins get `conflict.group.directory_owned` when they change a directory group (FR-014).
- Safety: the run stops before applying anything when the disables would exceed the threshold, keeps the cursors so the same changes are read again, and publishes `DirectorySyncNeedsAttention`. An unreachable directory or missing consent changes nothing, marks the run failed with the reason, and publishes `DirectorySyncNeedsAttention` on the third failure in a row. The tenant's last administrator is never disabled by the sync.
- While sync is active, Entra sign-in turns JIT off: the directory decides who has an account.
- The worker runs due syncs every minute per tenant (`DirectorySyncSchedulerService`); "sync now" goes through the outbox. Deleting the connection lists the synced users as a dependent and then removes the sync data, keeping the users.

### Differences and follow-ups
- Spec 003 scenario 6 calls the group source `directory`; the authz schema already has `idp` for groups owned by an identity provider, so the code uses `idp`.
- Deletion threshold is a pure percentage of active synced users. In a very small directory one leaver can trip the default 10 percent; the admin can raise it. A minimum absolute count is a product rule the spec does not give.
- Graph may send only changed properties on incremental reads, so a missing value keeps the previous one and an explicit null clears it. An account whose enabled state was never reported is treated as disabled.
- `DirectorySyncNeedsAttention` is published but no notification is sent yet, because notifications are a later spec.
- Manager link (reporting line) and the attribute mapping UI are not built; they belong with spec 010 and the directory admin page (T026), which is the next web task.
- The run holds its transaction open while Graph is read. This is fine for R1 directory sizes; a staged import can replace it if large directories need it.

## Entra organization mapping (spec 002 User Story 5)

### Built
- `IDirectoryReader` (Identity contract) gives Tenancy a snapshot of the directory mirror: each synced, enabled user with company, department, division, administrative units and groups, plus the directory's groups and administrative units. Directory sync now also reads `companyName`, `department`, `employeeOrgData.division` and Graph `administrativeUnits/delta`.
- Strategies for Entra ID: `attributes` (levels chosen from company, division and department, top first), `admin_units` and `groups` (selected groups). `ou_path` is refused with `validation.org_mapping.strategy_unavailable` until on-premises AD arrives in R2. One mapping per directory connection (`tenancy.directory_mapping`).
- Admin API: `GET`, `PUT /api/v1/org/directory-mapping`, `POST /api/v1/org/directory-mapping:preview` and `:approve`. Changing, previewing and approving need `org.manage` at tenant scope and the capability `identity.directory_sync`. Anyone who can view part of the hierarchy can read the mapping and run summaries (scenario 9). Any settings change clears the approval, so a new preview and approval are needed.
- Each run diffs the whole snapshot against what the connection owns, so it is idempotent. Preview stores the diff (unit keys, unit IDs and user IDs only) without changing data. After approval, the mapping is applied at once and then after each successful directory sync (`DirectorySynced` event).
- Units store `external_source = entra:{connection id}` and a stable key as `external_id` (normalized attribute path such as `company=acme gulf/department=finance`, or `admin_unit={id}` and `group={id}`). Codes are derived from the connection and key (`dir-` plus a hash), never from directory values. Unit and membership rows carry `owner = directory` and the connection ID.
- Values that differ only in case, spacing or Unicode form are one unit, named with the most common spelling (ties go to the ordinal first, so the name never depends on directory order); alias rules merge values; a value path can map to an existing product unit, which is used instead of creating one.
- Each mapped user gets one directory-owned primary membership. A mover gets the new primary first and the old one ended with today's date, so history is kept and `OrganizationMembershipChanged` is published. A person in several mapped administrative units or groups has no single primary unit and is listed as unmapped (`ambiguous`), like people with no value (`no_value`).
- A unit that disappears is deactivated only when no active children or memberships remain; otherwise it is flagged and left active. Disappearing units and memberships ended without a replacement are checked against the threshold (default 10 percent); a breach applies nothing and publishes `OrgMappingNeedsAttention`.
- Directory-owned units refuse rename of the default-locale name, type change, move and deactivate, and directory-owned memberships refuse end, with `conflict.org.directory_owned`. Names in other locales stay editable and survive syncs. Product units are never changed by the sync.
- Deleting the connection hands its units and memberships back to the product (owner `product`) and removes the mapping; nothing is deleted.

### Differences and follow-ups
- The plan describes a delta-driven sync with batches of 500 on the worker. This build diffs the full mirror snapshot in one transaction, which gives the same result and is idempotent; batching can come with the performance test (T027).
- "Values that differ only in language" (scenario 7) cannot be detected automatically; alias rules cover it.
- If a user already has a product-owned primary membership, the directory primary takes over and the product one becomes secondary.
- Not built yet: the mapping page and the "Managed by Active Directory" badge (T025), the hierarchy source setting (T026, FR-014), the performance test (T027), and a notification for `OrgMappingNeedsAttention`.
