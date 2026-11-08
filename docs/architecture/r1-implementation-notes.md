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

## Attribute conditions on grants (spec 004 User Story 7)

### Built
- Role assignments (`POST /api/v1/authz/assignments`) and resource permissions (`PUT /api/v1/authz/resources/{type}/{id}/acl`) take an optional `condition`. A comparison is `{"attribute": "job_grade", "operator": "gte", "value": 7}`; comparisons combine with `{"all": [...]}` or `{"any": [...]}` to a depth of two. Operators are `eq`, `ne`, `in`, `not_in`, `gte` and `lte` (the last two for numbers only). Anything else, including extra properties or expression strings, is rejected with `validation.condition.*`.
- The attribute catalogue lives in `Authorization.Contracts.ConditionAttributes` and matches spec 004 section 6. Values are normalized and stored as canonical JSON in a nullable `condition` jsonb column.
- Sources: membership type from tenancy; job title and office from the directory mirror (`DirectoryUserAttributes` in Identity); other user attributes through `IUserAttributeProvider`, which the HR integration (planned 019) will implement. Resource classification comes from a new optional `Classification` on `ResourceDescriptor`, and the owning unit from the descriptor. Request attributes (MFA, step-up age, network zone) have no source until spec 026 is built, so conditions on them fail closed.
- Explain returns `notApplied`: grants that cover the target but whose condition did not hold. Each reason carries the clauses, the expected and actual values and any missing attributes. Assignment, ACL and audit records include the condition.
- Org coverage, `HoldsAnywhere` and delegation checks count a conditional grant only when it holds without a resource, so a condition on a resource attribute never widens them.
- An inheritance rule that reaches a restricted resource through an org ACL entry still needs that entry's condition to hold.
- Attribute changes reach decisions through the existing 60 second cache expiry (FR-008).

### Interpretations and follow-ups
- FR-015 says a missing attribute evaluates "the condition" as false. This build reads that as the whole condition: if any attribute it names is missing, the grant does not apply, even when another branch of an `any` group would hold. This is the stricter of the two readings. Per-comparison evaluation would also be safe, since there is no negation, and is a small change if preferred.
- A stored condition that no longer validates (for example after an attribute is removed from the catalogue) never holds.
- Not built yet: the condition editor in the role assignment and sharing UI (spec 004 T017), and request attribute sources (spec 026).

## Connect Outlook and delegated access (spec 018 Phases 1 to 3, spec 003 FR-016)

### Built
- `integrations.delegated_token` (one row per tenant, user and session, RLS, cascade on connection delete) and `integrations.outlook_connect_request` (single-use state and browser binding as hashes, PKCE verifier cleared on use, refusal outcome). `connection_feature.options` holds feature options.
- `TokenProtector`: AES-GCM from the .NET base library, key `Integrations:Microsoft365:TokenKey` (base64, 32 bytes) from the platform secret store. The associated data is tenant, user and session, so a copied row does not decrypt. Tests use a random key per run.
- Employee API: `GET /api/v1/me/outlook` (state, features served, connected and expiry times; never the token), `GET /api/v1/me/outlook/connect?silent=&returnTo=` and `DELETE /api/v1/me/outlook`. Every endpoint needs `integration.microsoft365`. No TeamBeats permission is checked, because Microsoft 365 decides mailbox access for the signed-in user.
- Connect is an authorization code flow with PKCE against the TeamBeats application in the connection's directory, with `login_hint` set to the employee's email, the scopes of the features that are on and consented, no `offline_access`, and `prompt=none` for the silent reconnect. The callback is `/api/v1/integrations/outlook/callback` and is routed from the platform host to the tenant host as for admin consent. Only the same user, session and browser that started the connect can finish it.
- States: `ok`, `not_connected`, `reconnect` (token expired, or a feature turned on after connecting), `admin_setup` (feature not consented, or Microsoft answered `consent_required` or AADSTS65001), `conditional_access` (AADSTS53xxx, 50076, 50079), `turned_off`, `degraded` (Microsoft unreachable or no token key configured). A refusal is remembered for the session, so the employee is not sent back to a consent prompt.
- Clean-up, each audited as `integration.outlook_disconnected` with a reason and published as `OutlookDisconnected`: employee disconnect (all sessions of the membership), sign-out (new Identity event `SessionEnded`, that session), session revocation and membership disable (all sessions), a feature turned off or an option change that leaves a token with scopes no feature needs, connection disabled, and connection delete (foreign key cascade).
- Feature options: `PUT /api/v1/integrations/connections/{id}/features/{feature}/options` with `previewLine` and `maxMessages` (1 to 10, default 10) for Mail and `maxDays` (1 to 14, default 14) for Calendar, audited as `integration.feature_options_changed`. Preview line on swaps `Mail.ReadBasic` for `Mail.Read`, so the Mail feature goes back to pending admin consent.

### Differences and follow-ups
| Item | Status |
|---|---|
| Login hint | The employee's TeamBeats email, which directory sync keeps equal to the Entra mail or UPN. A UPN that differs from the mail is not read yet. |
| Two Microsoft 365 connections in one tenant | The oldest connection with a delegated feature on serves every employee. Choosing the connection by the issuer the employee signed in with (spec 018 edge case) needs the sign-in issuer on the session and comes with the merger case. |
| Tenant-wide session revocation | Revoking every session of a tenant publishes no per-user event. Those tokens cannot be used, because every use goes through a live session, and they are deleted after expiry by the next connect in the tenant. |
| Token key on staging | Until `Integrations__Microsoft365__TokenKey` is set as a Fly secret, connect answers `degraded`. |

## Fake external systems for development, tests and staging
- `src/DevFakes` holds in-process fakes of Microsoft Entra ID, Microsoft Graph (directory, consent, mail, calendar, free/busy, rooms) and OpenRouter. The test fakes `FakeIdentityProvider` and `FakeMicrosoftGraph` moved there from `tests/Testing`, so tests, local development and staging share one implementation.
- The development host points the adapters' named HTTP clients (`oidc`, `msgraph`, `openrouter`) at the fakes and rewrites redirects to `login.microsoftonline.com` to fake pages under `/dev/fake/entra`. The production adapters are unchanged.
- Staging must run with fakes on; production refuses any `Dev:Fakes` setting and never contains the assembly. Details, the `staging-demo` scenario and the control API are in `docs/testing/fake-systems.md`.
- New setting `Workers:DirectorySync:Enabled` (default true). Staging workers set it to false, because the fake directory lives in the staging API host, which runs the sync itself.
- Fixed in passing: the development host seeded the platform operator and the test tenant in one database session, which fails when both are configured (a session is one transaction). Each seeder now gets its own scope.

## My mail, My calendar and free and busy (spec 018 User Stories 2 to 4)

### Built
- Canonical ports in `Integrations.Contracts`: `IMailbox`, `ICalendar`, `IAvailability` and `M365States`. The Microsoft 365 adapters (`GraphMailbox`, `GraphCalendar`, `GraphAvailability`) are the only code that sees Graph JSON. They run as the session's membership with the token from `DelegatedAccessService.AcquireAsync`.
- `GET /api/v1/me/mail?view=focused|all&top=1..10&refresh=`: Inbox unread count and the latest messages, with sender, subject, received time, read, importance, attachments and the web link Microsoft returns, plus a compose link. Focused uses the Focused Inbox filter. `top` is capped by the admin maximum. The preview line is requested only when the option is on and the token holds `Mail.Read`. It is returned as plain text, without control characters, at most 200 characters.
- `GET /api/v1/me/calendar?days=1..14&hidePrivate=&refresh=`: Graph `calendarView` from the start of today in the tenant time zone, so series come expanded. Cancelled and declined items are hidden, free and tentative are marked, all-day items come first in each day, the join link and location are included, and private subjects are hidden when the placement asks. The range is capped by the admin maximum. Every item has source `outlook`.
- `GET /api/v1/people/{userId}/availability`: `getSchedule` as the viewer, for the rest of today. It returns status blocks only (busy, tentative, oof, working_elsewhere), merged and clipped to the day. A colleague who does not share gets `shared: false` and no blocks.
- Caching in memory only, keyed by tenant, user, session, feature and query: mail 2 minutes, calendar and free and busy 5 minutes. The last good answer of the session is kept for an hour and returned with its `asOf` time when Graph fails (`degraded`). A forced refresh is limited to 6 per employee per minute; beyond that the cache answers.
- Graph 429 and 5xx are retried up to 3 times as `Retry-After` asks, within a 3 second budget, then `degraded`. 401 maps to `reconnect`, 403 to `admin_setup`, and a mailbox Microsoft does not serve to `no_mailbox`.

### Differences and follow-ups
| Item | Status |
|---|---|
| Merged calendar (FR-017, T013) | Waits for the modules of specs 009 and 022. The calendar answers Outlook items only. |
| Who may see a colleague's availability | Until the people profile module (spec 010) exists, any active member of the same tenant. A user of another tenant is not found. |
| Calendar paging | One page of up to 250 occurrences, which covers 14 days for R1 calendars. |
| All-day items | Placed on the date Microsoft gives them, not shifted by the tenant time zone. |
| Telemetry `integration.graph_call` (T018) | Built. One histogram measurement (`TeamBeats.Integrations` meter, milliseconds) per Graph read an employee causes, tagged `tenant_id`, `feature`, `status` (`ok` or the failure kind) and `throttled` (Microsoft answered 429 on any attempt). Cached answers make no call and record nothing. No content, subjects, addresses or tokens. |
| Rate limit per employee (T018) | The forced refresh limit of 6 per employee per minute covers it; free and busy never forces a refresh. Room booking (R2) will need its own limit. |

## My mail and My calendar components (spec 018 T016)

### Built
- The Integrations module registers `outlook.my_mail@1` and `outlook.my_calendar@1` (category `personal`, at least 4 columns). Both need `integration.microsoft365` and declare their connection feature (`microsoft365:mail`, `microsoft365:calendar`). The registry checks the `kind:feature` form, and the add panel answers `validation.personalization.feature_off` while the feature is not usable, so they are offered only while Mail or Calendar is on.
- My mail properties: `view` focused or all (default focused) and `top` 1 to 10 (default 5). My calendar properties: `days` 1 to 14 (default 7) and `hidePrivate`.
- Web components in `components/placements`: unread badge, messages with unread, importance and attachment markers in text, a Focused and All switch, a New message link, days grouped as Today, Tomorrow and dates in the tenant time zone, all-day items first, free and tentative in text, Private appointment for hidden subjects, and a Join link that is the primary action from 10 minutes before the start until the end. Every link opens Outlook or the meeting in a new tab and says so to screen readers.
- Every state of section 10 comes from the shared state view: loading, empty, not connected and reconnect with Connect Outlook back to the current page, admin setup, no mailbox, turned off, conditional access with a link to Outlook, not in plan, failure with retry, and degraded with the time of the data shown and a retry that asks for `refresh=true`.
- Personal settings has an Outlook section that reads `GET /api/v1/me/outlook` when the panel opens: connected with Disconnect, not connected or reconnect with Connect Outlook, admin setup and managed device messages. It is left out when the server answers turned off, not in plan or a refusal.
- Browser tests `outlook.spec.ts` in English and Arabic with axe.

### Differences and follow-ups
| Item | Status |
|---|---|
| Browser tests and the Microsoft sign-in | The connect redirect needs HTTPS on the tenant host, so the browser tests answer the employee's mail, calendar and Outlook endpoints with their documented shapes. The server side of each state runs against the fake Graph in `MailCalendarTests`. |
| Focused or All as a saved preference (US2 scenario 2) | The switch starts from the placement's `view` and lasts for the page visit. Saving the choice per employee needs per-placement personal properties in the spec 008 preference profile, which do not exist yet. |
| Outcome after connect | The callback returns with `?outlook=<outcome>`; the components and the settings section show the new state, but the outcome itself is not announced yet. |

## Pages, templates and the component registry (spec 032 backend foundation)

### Built
- New `Experience` module and `Experience.Contracts` (schema `experience`). The 10 layouts and 12 templates are platform data in `Catalogue/catalogue.json`. Each template names its layout and gives every region a policy (allowed categories, minimum and maximum placements, protection, personal). No template or layout names a component, and an architecture test enforces this.
- Component registry built in memory at start from every `IComponentRegistration`. Manifests are validated for identity, platform compatibility (`^1`), trust tier (platform only), category, resource keys in English and Arabic, property schema, placement spans, approved context keys, known capabilities and permissions, events and data contract. A failed manifest is not registered and the reason is logged. The newest version of each major wins.
- Placement properties use a typed subset of JSON Schema (`string`, `text` as localized plain text, `number`, `integer`, `boolean`, `enum`, `url` limited to https and site-relative paths, `image` as an asset reference, `color_token`, `list`). No type accepts HTML. Markup tags and event handler attributes are refused in every text, and script schemes in URLs.
- Pages: create from a template, draft save with `If-Match` (409 with the stored version on conflict), validate, publish and roll back. Releases live in `page_release`, where the application role can only insert and read. Rollback copies an earlier release into a new one. Every save and publish runs the validator, and each finding names its region and placement.
- `GET /api/v1/experience/render?slug=` serves the latest release to viewers with `page.view`. Release snapshots are cached by tenant, page and release. Placements get only the context keys their manifest declares, with schema defaults applied. A component that is retired, turned off or not in the plan renders as `unavailable`, with no configuration. Provenance is included only for `config_history.view`.
- Permissions `page.view`, `page.edit`, `page.publish`, `template.edit`, `template.distribute`, `component.enable` and `config_history.view`. New built-in roles `page_editor` and `page_publisher`, added to existing tenants by migration. Employee gets `page.view`. Capability keys from section 9; `experience.max_custom_templates` is 5, 50 and unlimited.
- Two reference packs (`reference.alpha.*`, `reference.beta.*`) in the development host and tests only.

### Differences and follow-ups
| Item | Status |
|---|---|
| Manifest validation without a JSON Schema library | ADR-0012 point 3 says manifests are validated by a JSON Schema. The module validates a typed subset itself instead, to avoid a new dependency (CLAUDE.md rule 6). The manifest format stays JSON-compatible, so a schema can be published later. |
| Visibility rules | Audience rules come with spec 008. Until then a placement with `visibilityRuleRef` is refused, so no page can depend on a rule that does not exist. |
| Overlap, nesting and breakpoint order checks | Placements flow in region order with spans, so cells cannot collide yet. Sections, nesting and per-breakpoint order arrive with the renderer (T016). |
| Tenant templates, distribution and layout change (T010, T011) | Built, see "Tenant templates (spec 032 T010, T011)". |
| Manifest migration hooks (part of T012) | A module registers `ComponentMigration` hooks through `IComponentRegistration.Migrations()`, each from one major to the next of components it registered; any other hook is refused at start as `migration_range`. `POST /api/v1/experience/components/{id}/upgrade` with `fromMajor` (needs `component.enable` for the whole tenant) runs the hook over every page draft in the tenant that places `id@fromMajor`. A page whose converted placement is invalid for the new manifest, or whose hook throws, keeps its draft and is listed in `blocked` with the reason; pages without the component are not touched. Releases change at the next publish. Audited as `component.upgraded`. No platform or reference component has a second major yet, so the tests use a test-only component (`tests/Testing/UpgradePack.cs`). |
| Theme tokens in manifests | Token names are checked once the spec 033 token schema exists. |
| Denied publish outside scope | Recorded as `page.published` or `page.rolled_back` with outcome `denied`, then answered as not found. |

## Brands, tokens and the theme resolver (spec 033 backend)

### Built
- Token schema in `Experience/Themes/Tokens.cs`: semantic `color.*`, `font.*`, `space.*`, `radius.*`, `border.*`, `shadow.*`, `motion.*` and `state.*` tokens with platform defaults for light and dark mode and comfortable and compact density. Values are typed (color, dimension, approved font family per script, font weight, number, duration, named easing, shadow, alias). `url()`, expressions, free CSS and unknown fonts are refused with the token name. Stored as `$value` with `$modes` for the values that differ.
- Brand profiles (`brand`, `brand_version`). R1 publishes on save: every update is a new version, and the application role can only insert and read versions. `experience.max_brands` limits the count (1, 5, unlimited).
- Assignments and overrides per scope (`brand_assignment`), tenant or org unit by immutable ID, and locks (`experience_lock`). Resolution runs platform, tenant brand and overrides, then each unit from the root down. A child override of a value locked by a parent is refused at the API, and ignored by the resolver if older data holds one; the trace says so.
- Contrast checks on every save: normal text 4.5:1, large text and UI cues 3:1, focus indicators 3:1, body text at least 14 px. Shell and navigation pairs block, accent pairs warn unless the brand is strict. Findings name the token pair, mode, ratio and required ratio.
- `GET /api/v1/experience/theme?scope=` gives the effective tokens per mode and density, the locks, the brand assets and CSS URLs; provenance only for `config_history.view`. `GET /api/v1/experience/theme.css` serves `--tb-*` custom properties, private and immutable when the URL carries the content hash. The body font follows the document language (Arabic, Urdu, Devanagari stacks with fallbacks).
- First use seeds a default brand from the spec 001 primary color and logo, assigned at tenant scope with the default lock set (legal footer, security banner, accessibility minimums, sign-in presentation). The security banner and accessibility locks cannot be removed.
- Permissions `theme.edit`, `theme.publish`, `config_lock.manage`, role `brand_manager`, capabilities `experience.max_brands`, `experience.org_branding`, `experience.custom_domain`, `experience.token_extensions`. Audit `theme.published`, `brand.assigned`, `brand.unassigned`, `experience.lock_changed`; events `ThemePublished`, `BrandAssignmentChanged`, `ExperienceLockChanged`.

### Differences and follow-ups
| Item | Status |
|---|---|
| Who publishes in R1 | Without drafts and approval (R2), brands are a tenant library: `theme.edit` at tenant scope creates and edits them, and editing a brand that is in use also needs `theme.publish`. Applying a brand or overriding tokens at a scope needs `theme.publish` there, locks need `config_lock.manage`. |
| Contrast policy | Spec section 6 lets a tenant make accent pairs block. R1 holds this per brand (`strictContrast`), which is the smallest place to store it. |
| Cache invalidation | The theme cache key carries a per-tenant `theme_state` version, bumped on every brand, assignment and lock change, and the org hierarchy version, so moves and changes show on the next request without event consumers. |
| Invalid older values | A value that fails its type at render time is skipped, so the inherited value stays, and `theme.invalid_token` is counted and logged. |
| Tenant settings reader | `ITenantSettingsReader` gained `GetBrandingAsync` (primary color and whether a logo exists) for seeding. The plan said the contract gains nothing; reading the color needs one method. |
| Brand asset upload | Brands hold asset references (`tenant:logo` or `asset:{id}`). Upload with the spec 001 validator comes with the brand manager page (T011). |
| Experience impact on unit moves (US5) | The resolver follows the new parent once the move is saved. The impact preview before confirming is not built. |

## Page renderer in the web app (spec 032 T015 to T017)

### Built
- `/p/{slug}` renders the latest release of a page from `GET /api/v1/experience/render` and links the effective theme CSS of the page's scope (light and dark by `prefers-color-scheme`). The render answer now carries `themeScope`. Pages of another tenant, or that the viewer may not see, answer 404.
- `lib/experience`: typed render client, the registry (`id@major` to a lazy import from `components/placements`), the page-scoped event channel and the placement contract (own props, declared context keys, events).
- Placement decorator: error boundary with a failed state, a Suspense placeholder that keeps the space, an unavailable state for placements the API marked so or whose code is missing, and a failure signal that carries only the component, placement and kind.
- Layouts: rows of a 12-column CSS grid built from the regions' spans and section modes (contained, fixed, fluid, full bleed), regions and rows as size containers, logical properties only, the `stack` and `two_then_stack` fallbacks by container width, and WAI-ARIA tabs for the panel layout (L09) with arrow keys that follow the reading direction.
- `npm run verify` now also runs `check:placements`: no placement imports another, and only the registry loads placement code.
- Playwright: both reference packs on all 12 templates in English and Arabic at 320, 375, 768, 1024, 1440 and 1920 px with no horizontal scroll, axe on two templates per language, a throwing, slow and missing component next to working ones, events between unrelated components, and cross-tenant not found. The shell header now wraps at narrow widths, which the 320 px check found.

### Differences and follow-ups
| Item | Status |
|---|---|
| Event payload schemas | The channel allows only declared event names and caps payloads at 4 KB of JSON. Checking payloads against the manifest schemas needs the schemas in the render answer; that comes with the builder slice. |
| Reference packs in the web build | Included only when `NEXT_PUBLIC_TB_REFERENCE_PACKS=true`, which the Playwright stack sets. Production builds leave them out. |
| Failure telemetry sink | Failures are a `tb:placement-failure` DOM event for now; shipping them to the API is T021. |
| Visibility rules | Still refused at save (spec 008), so the renderer has nothing to evaluate yet. |
| Brand assets | The banner draws a labelled area because asset delivery is not built yet. |

## Theme tokens in the web app (spec 033 T010)
- `src/Web/app/tokens.css` holds the platform defaults (light, and dark under `prefers-color-scheme`) as `--tb-*` custom properties. It is generated from the token schema by `tests/Unit/WebTokensFileTests.cs`, which fails when the file drifts; regenerate with `TEAMBEATS_UPDATE_TOKENS=1`.
- All web styles now read only these tokens. Stylelint refuses hex and named colors, color functions and absolute units such as `px`; `rem` stays for layout widths and breakpoints. `npm run check:tokens` refuses any custom property that is not a schema token or a local `--xp-*` layout property, and refuses setting a `--tb-*` token in our own CSS.
- The root layout links the tenant theme for signed-in viewers, and a page owned by an org unit adds the theme of its scope after it. The stylesheets are render-blocking and hoisted, so the first paint already has the final tokens. Signed-out pages keep the platform defaults.
- Playwright `theme.spec.ts`: platform tokens when signed out, the tenant accent in light and dark when signed in, and a layout shift score below 0.1.

### Differences and follow-ups
| Item | Status |
|---|---|
| Density | The web app uses the comfortable variant until the personal mode and density choice of spec 008 is built. |
| Shell colors | The header still uses surface tokens; the shell tokens (`color.shell.*`, `color.nav.*`) come into use with the spec 008 shell. |

## Brand manager (spec 033 T011)
- `/admin/branding` (navigation section `branding`, permission `theme.edit`): token groups with light, dark and compact values where the schema allows them, color pickers, approved fonts and easings as choices, the contrast and size table with ratios in text, brand images, and a live preview of a sample shell, navigation, card, link, buttons and states in light and dark, left to right (English sample) and right to left (Arabic sample).
- The page applies no theme rules. `GET /api/v1/experience/tokens` gives the schema with defaults, allowed modes, approved fonts, easings and asset slots. `POST /brands/{id}/check` now also answers `preview`, the CSS custom properties per mode as the theme CSS would serve them, which the preview panes apply directly. Brand views carry `assetUrls`.
- Brand images: `POST /api/v1/experience/assets` (raw body, `theme.edit` at tenant scope) checks the file with the spec 001 rules, now shared as `ImageValidator` in Primitives (PNG, JPEG, static SVG, 1 MB). Files go to tenant-prefixed storage under `experience/assets/`, rows in `experience.brand_asset` are insert-only, and uploads are audited as `brand.asset_uploaded`. `GET /api/v1/experience/assets/{id}` serves them to members of the tenant, immutable and private, with `nosniff` and a sandboxing CSP for SVG. A brand version may refer only to this tenant's uploaded assets or `tenant:logo`.
- Playwright `brand-manager.spec.ts` in English and Arabic: live preview in both modes and directions, a blocking contrast row refused on save, logo upload and an unsafe SVG refused, save as a new version that becomes the tenant theme, no horizontal scroll at 320, 375 and 1440 px, and axe.

### Differences and follow-ups
| Item | Status |
|---|---|
| Tenant settings link | The spec 001 settings screen is still the generic section page; its link to the brand manager comes with that screen. |
| Asset preview sizes | The preview shows the logo in light, dark and both directions; mobile and desktop shell previews come with the spec 008 shell. |
| Org overrides and locks | Managed through the API; their screens come with preview as organization (R2) or the shell slice. |

## Shared states and interaction primitives (spec 008 T001 to T003)
- `components/ui`: `StateView` for the seven shared states (loading, success, empty, error, permission denied, offline, unsupported) with platform wording under `ui.state.*`; `Skeleton` sized by the caller and announced once; `Button` with primary, secondary, quiet and destructive variants and a busy state; `ConfirmAction`, where the first press only asks, focus goes to the safe choice and Escape cancels; `MessagesProvider` and `useMessages` with critical messages that persist and are announced assertively, and normal and low messages that never block, dismiss themselves (10 s and 5 s, 15 s with an action such as undo) and pause while focused or hovered; `SelectionBar` with count and consequence in text; `FormField` hints.
- Placement states in the renderer now use `StateView`.
- Buttons are at least 44 by 44 CSS pixels. Reduced motion stops animation and transitions globally. Density comes from the theme variant, so both densities use the same components.
- `/ui-gallery` shows all of them for tests and exists only with the development fixtures (`NEXT_PUBLIC_TB_REFERENCE_PACKS=true`). Playwright `interaction.spec.ts` in English and Arabic covers the states, one primary action per context, the destructive look, linked and announced errors that keep values, bulk selection, confirm and undo, message priorities with a fake clock, both densities at a 640 px viewport (200% zoom of 1280 px) and reduced motion.

### Differences and follow-ups
| Item | Status |
|---|---|
| Offline handling | The offline state view exists; keeping unsaved preference edits while offline comes with preferences (T007 to T010). |
| Personal density and motion choice | Comes with the preference profile; until then density and motion follow the theme and the device. |

## Shell, menus and information architecture (spec 008 T004 to T006)
- Tables in the Experience module with forced RLS: `shell_config` (one insert-only row per published version), `nav_menu` (one row per kind and scope: `global`/`tenant`, `organization`/unit id, `section`/node id), `ia_node` (labels per language, a unique slug, either a page or a link, an owning organization and a position) and `slug_redirect` (rows are only written and removed).
- New permissions `shell.edit`, `navigation.edit` and `help.edit`. The tenant administrator gets all three; the organization administrator gets `navigation.edit` within their subtree.
- Endpoints under `/api/v1/experience`: `GET /shell` (layout, global menu, the organization menus of the caller's units and their parents, and the caller's organizations), `GET` and `PUT /shell/definition`, `GET /menus`, `PUT /menus/{kind}/{scope}`, `GET /ia`, `PUT /ia/{id}`, `GET /resolve?path=` and `GET /quick-nav?q=`. Every publish writes a new version on its own, is audited (`shell.published`, `navigation.published`, `navigation.node_changed`) and emits `ShellPublished` or `NavigationPublished`. Nothing here touches pages.
- Menus, breadcrumbs, section menus and quick navigation list only entries the caller may open: a link entry always, a page entry when the page is published and the caller holds `page.view` at its scope, and only when every parent entry is visible too. Labels follow the reader's language.
- Resolve gives a page, a redirect (an old slug after a rename, or a link entry), or one recovery answer (home and search) that is the same for missing, unpublished and not permitted destinations. Pages outside the IA stay reachable by their own slug under the same rule.
- Editing a global menu or a tenant entry without permission answers forbidden; an organization or section scope outside the caller's grants, or of another tenant, answers not found.
- The shell layout holds the placement of registered regions and utilities only; `identity` and `settings` must stay placed so language, mode, density and accessibility remain in one place. Utility components belong to their own modules.

### Differences and follow-ups
| Item | Status |
|---|---|
| Quick navigation over search | Matches IA labels and slugs and published page slugs until search (spec 012) takes over. |
| Help content | `help.edit` exists; editing contextual help content is not built yet. |
| Organization switching | The shell answers the caller's organizations and whether switching applies; the switcher comes with the shell frontend. |

## Preferences and personal layouts (spec 008 T007 to T010)
- Tables in the Experience module with forced RLS: `personalization_policy` (per page and region: rights, an optional audience rule, the personal placement limit), `preference_profile`, `personal_layout` (the user's operations per page with a revision) and `audience_rule`.
- New permissions `personalization.policy` (tenant administrator; organization administrator in their subtree) and `personal_layout.edit` (tenant administrator and the default Employee role). New capability keys `experience.personalization`, `experience.audience_variants`, `experience.guided_tours` and `experience.behavioral_personalization` with the values of spec 008 section 9.
- Default policy: with no policy row, a region that the template marks personal is delegated to everyone with every right and a limit of 12, and any other region is not personalizable. The default home template (T013) carries the personal region, so new tenants get home personalization without a stored row. Locked regions (protected or mandatory) can never be delegated, and regions that are not personal need `experience.personalization`.
- Endpoints:
  - `GET` and `PUT /api/v1/me/preference-profile` (mode, density, motion, text scale; only schema keys and values; all cross-device in R1). Language stays on `PUT /api/v1/me/preferences` of the user account, which other modules already read.
  - `GET`, `PUT` (with `If-Match`) and `DELETE /api/v1/me/pages/{pageId}/layout`, `DELETE /api/v1/me/layouts`, `GET /api/v1/me/pages/{pageId}/add-panel?region=`.
  - `GET` and `PUT /api/v1/experience/pages/{pageId}/personalization-policy`, `GET /api/v1/experience/audience-rules`, `PUT /api/v1/experience/audience-rules/{id}`, `POST /api/v1/experience/audience-rules/preview`.
- Personal layouts are operations (`add`, `hide`, `remove`, `move`) over the latest release. A save replays them against the viewer's rights and the spec 032 component checks and refuses the whole save with 422 and the reasons when any fails. At render, operations that no longer apply are skipped and listed in `skipped`, not deleted. Personal placement IDs start with `my-`.
- Precedence (FR-031), covered by tests: locks and package or legal constraints (a disabled component is unavailable wherever it is placed), then audience rules (a placement outside the viewer's audience cannot be reached by an operation), then the published arrangement, then the user's operations where the policy allows them.
- Audience rules: org units (with their subtrees), membership types, languages and user attributes from the spec 004 catalogue. Targeting never grants or removes access. A region with targeted placements must keep at least one untargeted placement as the default (blocking finding `audience_default_missing`). Visibility rules need `experience.audience_variants`. The targeting preview takes synthetic personas and reads no real user's data.
- Rendered pages now carry `personal` on placements, `rights` on regions and `skipped` on the page.

### Differences and follow-ups
| Item | Status |
|---|---|
| Groups and roles in audience rules | There is no group contract yet, and role names are never checked, so R1 rules use org units, membership types, languages and attributes. |
| Preference endpoint path | Appearance preferences use `/me/preference-profile` because `/me/preferences` already holds the language on the user account. |
| Undo | The command stack of the last 20 changes is kept by the frontend (T012). |

## Shell frontend (spec 008 T011)
- `features/shell`: `Shell` renders the regions of the published shell definition in their slots (start, center, end and below), with a fallback layout when the shell API is unavailable. `components/shell/Header` now renders the shell, so the home page, published pages and administration share one frame.
- `GlobalMenu` is a disclosure menu for the global and organization menus: links stay links, entries with children have their own toggle, Escape closes and returns focus, a click outside or focus leaving closes it. Below 48rem the same list sits behind one menu button. Link owners show next to same-named labels (FR-015).
- `app/[...path]` resolves IA addresses through `GET /api/v1/experience/resolve`: pages render with breadcrumbs and the section menu, redirects follow, and anything else shows the one recovery view with a link home.
- `QuickNav` lists permitted destinations from `GET /api/v1/experience/quick-nav` as the user types, announced by count.
- `SettingsPanel` holds language, appearance mode, density, motion and text size in one place on every page (FR-010). The root layout applies them: mode and density pick the theme variant links, `data-motion="reduced"` stops animation like the device setting, and text size scales the root font.
- The organization indicator shows when the person belongs to more than one organization. Notifications, the task badge, contextual actions and utilities render nothing until their modules register components.
- Playwright `shell.spec.ts` in English and Arabic: the menu by keyboard, Escape and outside click, breadcrumbs and section menu on an IA address, old slug redirect, recovery, quick navigation, settings applied and kept after reload, the phone menu without horizontal scroll, and axe. The language test in `admin-shell.spec.ts` now opens the settings panel first.

## Employee home and personalization mode (spec 008 T012, T013 in part)
- Home template `H01` (layout L06) joins the catalogue next to T01 to T12: a protected top area for mandatory and critical items and a personal area (`personal: true`, up to 24 items). With no policy row the personal area is delegated to everyone in every package, and the top area never is.
- The shell definition carries `homePageId`, published with `shell.edit` and audited with the shell. It must name a page of the tenant (`validation.shell.home_unknown`). `GET /api/v1/experience/shell` returns `home`, the page slug, only when the page is published and the caller holds `page.view` at its scope. The root address renders that page with the caller's layout; without one it keeps the plain welcome.
- The add panel now returns the component name, description and refusal reason in the caller's language. R1 adds a component with its defaults, so one with a required setting and no default is listed with `validation.personalization.needs_setup` until personal settings exist.
- `features/personalization`: a provider holds the operation list, its revision and an undo stack of the last 20 lists the API accepted. Every change sends the whole list with `If-Match` and the page re-renders from the API, so protection and policy are never decided in the browser. A stale revision reloads the layout and says so; a refusal announces the API's reason. Repeated moves of one item collapse into one operation, and removing one of the user's own items drops every operation about it.
- Each item the user may change gets a labelled group of buttons (move up, move down, move to another area, hide or remove); each delegated area gets an add button and panel. Focus follows a moved item, and goes to a neighbour when an item leaves. A polite live region announces the mode and each result. Reset this page and reset all pages (in the settings panel) ask for confirmation first and keep language and the other preferences.
- `PageRenderer` accepts optional region and placement tools; it gives them a place and never knows what they do.
- Playwright `home-personalization.spec.ts` in English and Arabic: keyboard-only move, hide and add, announcements, the layout after a reload, three undos in reverse order, reset with confirmation, leaving the mode, the phone width without horizontal scroll, and axe in the mode.

### Differences and follow-ups
| Item | Note |
|---|---|
| Drag and drop | R1 reorders with buttons only, which gives the same result for keyboard and pointer (FR-025). Drag can come with resize in R2. |
| My mail and My calendar | Modules suggest components for the default home through `IComponentRegistration.PersonalHomeDefaults`, because templates never name components (spec 032 FR-014). The Integrations module suggests My calendar, then My mail. They come first in the first personal region (`grid` in `H01`) as personal placements with fixed IDs (`home-outlook-my-calendar`, `home-outlook-my-mail`), only while the component is available to the viewer, its feature is on and the viewer may add to the region. Employees move or remove them like their own placements, and a removal stays removed. |
| Employees without a mailbox (FR-051) | TeamBeats learns that an employee has no mailbox only when Microsoft answers for them, so the home still shows My mail and My calendar, and they show the no work mailbox state. |
| Default home page | A tenant gets the template, not a page: an administrator creates a page from `H01` and sets it as home. |
| Hidden items | A hidden item returns through undo or reset; there is no list of hidden items in R1. |

## Tool box utilities (spec 008 T014)
- `Components/PlatformComponents` registers three platform manifests through the same `IComponentRegistration` as any module: `toolbox.world_clock@1`, `toolbox.time_converter@1` and `toolbox.weather@1`, category `utility`, no package capability, and every property has a default, so they can be added from the personal add panel.
- The world clock shows up to six IANA zones (default: the viewer's own zone and UTC) with localized zone names and digits, ticks once a minute after hydration, and leaves out a zone the browser does not know. The converter turns a date and time in one zone into another in the browser, checking the offset twice so daylight saving edges land right. Neither sends anything to the server.
- Weather uses Open-Meteo (decision 2026-11-07). The canonical port `IWeather` lives in `Integrations.Contracts`; the adapter `Integrations/Weather/OpenMeteoWeather` looks the place up, then reads current weather and today's high and low, and maps WMO codes to the conditions the component names. Only the place name leaves the platform. `GET /api/v1/weather?location=` (any signed-in user, 1 to 100 characters, `refresh=true` for retry) answers `ok`, `not_found`, `degraded` (the last good answer, kept 6 hours) or `unavailable`. Answers are cached per tenant, language and place for 15 minutes; a failure pauses calls for a minute. The manifest declares the endpoint as its data.
- Settings sit under `Integrations:Weather` (`Provider`, base URLs, optional `ApiKey`). The free Open-Meteo endpoints are for non-commercial use; a paid plan sets its own base URLs and key there, and another provider is another `IWeather` adapter.
- `DevFakes/FakeOpenMeteo` answers for Dubai, Riyadh, Cairo and London (Arabic names when asked) with fictional readings. Development, staging and the integration tests use it; staging never calls the real service. `/dev/fake/outage/open-meteo` turns it off.
- The component shows the city of the viewer's time zone when no place is set, and the shared loading, empty, error (with retry) and stale states.
- Playwright `toolbox.spec.ts` in English and Arabic with axe; unit tests cover the manifests and weather codes, and integration tests cover adding the clock to the home and the weather states, tenant cache and validation.

### Differences and follow-ups
| Item | Note |
|---|---|
| Weather plan | The free Open-Meteo tier is non-commercial. A paid plan is needed before customer production use; it is configuration only. |
| Shell utilities | The shell keys `world_clock`, `time_converter` and `weather` exist; placing the components in the shell's utilities region comes with the utilities slot rendering. |

## Shell and home accessibility tests (spec 008 T015)
| Need | Where it is tested |
|---|---|
| Menus by keyboard: open, move in, Escape back to the toggle, close when focus or a click leaves | `shell.spec.ts`, `shell-reachability.spec.ts` |
| Screen reader: skip link reaches the content, one main landmark, named navigation, page title, `aria-expanded` and `aria-controls` on toggles, closed submenus out of the reading order, live announcements | `shell-reachability.spec.ts`, `home-personalization.spec.ts` |
| Personalization by keyboard only: move, hide, add, undo, reset, focus following the item | `home-personalization.spec.ts` |
| Mobile reachability at 375px: every shell control reached by Tab and on screen, the phone menu by keyboard, no sideways scroll, personalization tools fit | `shell-reachability.spec.ts`, `shell.spec.ts`, `home-personalization.spec.ts` |
| axe with no critical findings in `ar` and `en`, desktop and phone | all of the above, plus `toolbox.spec.ts` and `interaction.spec.ts` |

Screen reader checks assert the roles, names and states that assistive technology reads. They do not replace a manual pass with NVDA and VoiceOver, which is a follow-up before the R1 release.

## Tenant templates (spec 032 T010, T011)

### Built
- Tables `experience.template` (ID, the platform template it copies and its version, distribution as org unit IDs) and `experience.template_version` (version number, `draft`, `released` or `deprecated`, and the stored definition: names and descriptions per locale, layout, variants and region policies). Both are tenant tables under forced row-level security.
- `TemplateStore` finds the template a page names, platform or tenant, by ID and version, and is used everywhere pages, the picker, previews, rendering and personalization looked at the platform catalogue before. A page keeps the version it records (FR-018).
- `TemplateService` and `/api/v1/experience/templates`: list, get, create (copy of a released platform template), `PUT {id}/draft`, `POST {id}/release`, `POST {id}/deprecate` and `PUT {id}/distribution`. Saving after a release starts the next version as a draft. Variants must come from the base template, every layout region needs a policy once, and protection is `optional`, `mandatory` or `protected`.
- `experience.max_custom_templates` (5 on Workplace, 50 on Workplace Plus, unlimited on `enterprise_gov`) answers 403 `entitlement.quota_exhausted` with the capability key.
- Distribution (FR-020): an empty list is the whole tenant. Otherwise a page may use the template only when its org unit is inside one of the listed subtrees, and the chooser in `GET /catalogue` lists the template only for editors who can edit pages in one of those subtrees. Everywhere else the template answers as `validation.page.template_unknown`, the same as a template that does not exist.
- Versions (US5 scenarios 3 and 6): `GET /pages/{id}` carries `newerTemplateVersion` when a later version is released, which the owner applies through `change-template` with its preview, and `templateDeprecated` when the page's version is deprecated. Deprecated pages keep rendering; new pages and template changes cannot use the template.
- Audit `template.released`, `template.distributed`, `template.deprecated` and `region.lock_changed` (one entry per region whose protection changed, with before and after). Events `TemplateReleased` and `TemplateDeprecated` through the outbox.
- Tests: `TemplateTests` (distribution to the Gulf, the limit, version 2, deprecation, removal of a protected region, input checks, another tenant).

### Differences and follow-ups
| Item | Note |
|---|---|
| Designer scope | Template designers need `template.edit` for the whole tenant, and distribution needs `template.distribute`. Templates owned by one organization's designers (`template.edit` at org scope, spec 032 section 3) are a follow-up. |
| Protection levels | Every region of the template must stay on the page (`validation.page.region_missing`), and region policies always come from the template, never from the page. `mandatory` and `protected` regions are never delegated to personalization. Regions "removable by named delegated roles" (FR-019) need a role list in the policy and are not built. |
| Retired state and migration guidance | FR-021 also names a `retired` state and migration guidance. R1 has draft, released and deprecated; the page view only says that its version is deprecated. |
| Platform template versions | The platform catalogue holds one version of each platform template. A page that records another version gets `validation.page.template_mismatch`. |
| Designer screens | `/admin/templates` lists the organization's templates and copies a platform template; `/admin/templates/[id]` edits names, variants, region locks, policies and distribution, and releases or deprecates. The section shows only for template designers of the whole tenant. The page editor has a "Template and layout" panel that says when a newer version is released or the version is deprecated, works the change out, lets the editor place or remove each component without a region, and applies it to the saved draft. Playwright: `templates.spec.ts` in `en` and `ar`. |

## Performance budgets and renderer telemetry (spec 032 T021)

### Built
- Playwright `performance.spec.ts` fills all 12 reference templates with both reference packs and checks LCP 2.5 s or less, INP 200 ms or less and CLS 0.1 or less (FR-058, SC-005) on two reference profiles: mobile (375 by 812) and desktop (1440 by 900). Each page is opened once before it is measured, so the development server's first compile is not counted. The measured values are attached to the report.
- Renderer telemetry: `PageRenderer` mounts `PageTelemetry`, which collects LCP, CLS (largest session window) and INP (longest interaction) from the browser's own performance entries, without a library, and sends one `page.web_vitals` event with template, layout and device class when the page is hidden. Placement failures (error, missing, undeclared event, invalid payload) are sent as `placement.render_failed` with the page key and release. Events go in batches of at most 20 with `sendBeacon`.
- `POST /api/v1/experience/telemetry` (signed-in users) accepts only the declared shape: identifiers that match their patterns, known kinds and device classes, and numbers in range. Anything else is dropped and counted. Accepted events become OpenTelemetry metrics (`experience.placement.render_failed`, `experience.web_vitals.lcp`, `.inp`, `.cls`) tagged with the tenant from the session; a render failure also writes a log line with the page key and correlation ID. The `TeamBeats.*` meters are now exported with the rest of the metrics.
- Tests: `PageTelemetryTests` (metrics tagged with the session tenant, free text and out-of-range values dropped, session required, batch limit) and the telemetry check in `performance.spec.ts`.

### Differences and follow-ups
| Item | Note |
|---|---|
| Reference profiles | The spec asks for "agreed reference profiles", which are not defined. The two viewports above, on the CI runner without CPU or network throttling, are the smallest safe default. Throttled profiles need an agreed device and network. |
| Lab, not field, values | CI measures one load per template on the development server. The p75 by mobile and desktop segments comes from the `page.web_vitals` metrics once a metrics backend receives them. |
| Asset budgets (FR-060) | Script, font and image size budgets need a production build in CI and are not checked yet. |
| Telemetry retention | Spec 032 section 10 sets 30 days for frontend error events. Nothing is stored by the API; retention belongs to the metrics and log backend. |

## Page builder (spec 032 T018 to T020)
| Part | Where |
|---|---|
| Pages list, template chooser with layout preview, text description and regions | `/admin/pages`, `/admin/pages/new`; `GET /api/v1/experience/pages`, localized `GET /api/v1/experience/catalogue` |
| Editor: regions, picker with reasons, settings drawn from the manifest schema, action menu and shortcuts, undo and redo, autosave with If-Match | `/admin/pages/[id]`, `features/builder/editor` |
| Labels for settings | Picker items carry `labels`, from the resource keys `<component name key without .name>.props.<property>` and `.props.<property>.<option>`; a property with no resource is labelled by its schema name |
| Previews by device and direction | `GET /api/v1/experience/pages/{id}/preview?locale=`, drawn at `/preview/pages/[id]` in a frame at phone, tablet or desktop width |
| Checks | Every finding carries its localized `message`; translation status per enabled language comes from `validation.page.translation_missing` warnings with `locale` |
| Tests | `page-builder.spec.ts` in `ar` and `en`; `PageCompositionTests` |
| Layout change (US5 scenario 7, part of T011) | `POST /api/v1/experience/pages/{id}/change-template` works the change out without saving; with `apply` and If-Match it saves it. Each placement keeps the region with the same ID when it fits (category, minimum width, room), else goes to a fitting region of the same section, else is listed in `unplaced` with the regions it fits, as a blocking `validation.page.placement_unplaced` finding. `assignments` maps a placement to a region, or to an empty value to remove it. A width wider than the new region falls back to the component default; the variant is kept when the new template has it. Only the draft and the page's template change; the live release changes at the next publish. Audited as `page.template_changed` (spec 032 section 13 does not list it; recorded here as the smallest safe default, since a template change is not a publish). |

### Differences and follow-ups
| Item | Note |
|---|---|
| Unsaved edits | Kept in the browser's local storage under `tb.builder.<tenant>.<page>` while a save fails or the browser is offline, and saved when it is back online. A copy from an earlier visit is restored and saved against the version it started from, so a conflict shows if the page changed meanwhile. |
| Save refusals | The API refuses to save a draft with blocking findings, such as a required text left empty. The editor keeps the edit, shows the findings and saves once they are fixed. |
| Preview | Shows the last saved draft, with no audience targeting or personalization. Component text follows the preview language; component chrome follows the editor's own language. Persona previews (spec 008 audiences) are a follow-up. |
| Translation status (FR-047) | A localized text with a value in one enabled language and not another is a warning. It never blocks publishing, because there is no tenant policy for required languages yet. Page labels and navigation labels are not checked yet. |
| Accessibility findings (FR-046) | The builder shows every validator finding per page, region and placement with its text, and publish stays blocked while any is blocking (required texts such as image descriptions, markup in text, unsafe links). Manifests carry no accessibility metadata yet, so focus order and theme contrast are not checked per placement; contrast is checked by the brand manager (spec 033). |
| Images | Image settings take an `asset:<id>` reference typed by hand until the asset picker comes. |
| Revision history across sessions (FR-043) | Releases and rollback exist; a history of draft revisions is a follow-up. |

## Backfilling tenant rows in migrations
Tenant tables force row-level security, so a migration that reads or writes their rows without a tenant set sees nothing. Authorization migrations 0003 to 0006 meant to add new permissions and roles to existing tenants and changed nothing; tenants created before them (the staging tenant among them) had administrators without page, theme, shell and personalization permissions. Migration 0007 applies those additions once per tenant with `app.tenant_id` set, and `PermissionBackfillTests` covers it. A future migration that changes rows of existing tenants loops over the tenants the same way.

## Notifications and email (spec 015 T001 to T008)
| Part | Where |
|---|---|
| Send contract | `INotificationSender.SendAsync` in `Notifications.Contracts`, in the caller's transaction. Recipients who are not active members of the current tenant are skipped and counted. Links must be paths on the tenant host. |
| In-app centre | `GET /api/v1/me/notifications` (filter, category, cursor), `/count`, `/{id}/read`, `/read-all`; text is rendered in the reader's language at read time |
| Dedupe and grouping | Same dedupe key for the same user within 1 hour is dropped; the same group key on an unread entry within 15 minutes adds to its count and sends no new email |
| Email | `delivery` rows are the queue; `NotificationDeliveryWorker` runs per tenant every 5 seconds, digests every minute, purge daily. Retries after 1, 5, 15 and 60 minutes, then every 3 hours, and the delivery fails after 24 hours with an error log. A 5xx recipient refusal pauses the address. |
| Preferences | `GET/PUT /api/v1/me/notification-preferences`; mandatory categories (platform and tenant) always email and ignore changes |
| Tenant settings | `/admin/notifications`; `GET/PUT /api/v1/notifications/settings` with a version, relay test, template overrides. A tenant relay needs `notifications.tenant_smtp` (workplace_plus and enterprise_gov) and stores only a secret reference. |
| Hosts | Production API and workers send with SMTP; `Notifications:Email:Mode` `Capture` is refused in Production. The development host captures mail and runs the delivery worker in process (`Notifications:Delivery:InProcess`); staging workers turn theirs off. `/dev/mailbox` shows captured mail. |
| Tests | `NotificationsTests`, `NotificationRulesTests`, `PermissionBackfillTests`, `notifications.spec.ts` in `ar` and `en` |

### Differences and follow-ups
| Item | Note |
|---|---|
| Live count | Section 15 named a server-sent events connection that does not exist; the bell polls the count every 30 seconds while the page is visible. The spec wording is corrected to that. |
| Bounces | Only synchronous refusals count as hard bounces. Delivery status notifications that arrive later by email are not read in R1. |
| Platform relay | No provider is chosen yet (spec 015 section 17). Production needs `Notifications:Email:SmtpHost` and a from address with SPF, DKIM and DMARC before email is turned on. |
| Template editor screen | The template override API exists; the admin screen lists no templates yet because no sending module defines any. It comes with the first sending module (T009). |
| T009 | Wiring existing notify points, the 100,000 burst test and spec evidence wait for the sending modules. |

## Working calendars (spec 022 T001 to T007)
| Part | Where |
|---|---|
| Data | `hr.working_calendar` (name in each language, country, ISO week days, owner unit, one tenant default), `hr.calendar_holiday` (date, name, half day), `hr.calendar_assignment` (one calendar per org unit); forced RLS on all three |
| Contract | `IWorkingCalendar` in `Hr.Contracts`: `ForUnitAsync`, `ForUserAsync`, `TenantDefaultAsync` return a `WorkingCalendarSnapshot` with `IsWorkingDay`, `WorkingFraction`, `CountWorkingDays`, `AddWorkingDays` and `NextWorkingDayOnOrAfter` in the tenant time zone |
| Resolution | The nearest assigned unit on the way up the hierarchy wins, then the tenant default, then a built-in Monday to Friday week |
| API | `/api/v1/hr/calendars` (list, create, get, update and delete with `If-Match`), `PUT /calendars/{id}/holidays` (replaces one year), `/assignments`, `PUT /assignments/{orgUnitId}`, `GET /working-days` preview of up to 366 days |
| Access | `hr.calendar_manage`, granted to the tenant administrator and the new built-in role HR officer (`hr_officer`); migration 0009 adds both to existing tenants. A scoped HR officer edits calendars owned in their subtree and assigns calendars within it; tenant calendars need whole tenant coverage to edit. |
| Events and audit | Every change audits `hr.calendar_changed` and publishes `WorkingCalendarChanged` with the affected dates and units |
| Web | `/admin/calendars`: calendar list, working week picker, holiday table per year, unit assignment and working-day preview |
| Tests | `WorkingDayTests`, `WorkingCalendarTests`, `PermissionBackfillTests`, `calendars.spec.ts` in `ar` and `en` |

### Differences and follow-ups
| Item | Note |
|---|---|
| Assignment target | The spec does not say whether a calendar attaches to a unit or a location. Calendars attach to org units and children inherit; locations can be added when 010 models them. |
| Owner unit | A calendar with no owner unit is a tenant calendar. A calendar owned by a unit can be assigned only within that unit's subtree. |
| Tenant default | Created on first use with Monday to Friday and no holidays, so every tenant always has one. It cannot be deleted. |
| Snapshot cache | Plan T005 named a cache invalidated by `WorkingCalendarChanged`. Snapshots are cached for one request only, so no invalidation is needed yet; a shared cache can follow if leave (R2) needs it. |
| Not built | Week start preference and Hijri date display. |

## People directory backend (spec 010 T001 to T006, T007 and T008 in part)
| Part | Where |
|---|---|
| Data | `people.profile` (one row per tenant membership: account values copied from Identity and Tenancy, directory, person and administrator values, location, hire date, normalized search text), `people.reporting_line` (history with dates), `people.field_policy`, `people.location`, `people.settings`; forced RLS on all |
| Account read model | Names, emails, status and units are copied through the `IUserDirectory`, `ITenantMemberships` and `IOrganizationHierarchy` contracts whenever the hierarchy version moves (membership and unit changes bump it), and per person on `UserProvisioned`, `UserProfileSynced` and `UserDeprovisioned` |
| Directory | Directory sync now reads `manager`, `businessPhones`, `mobilePhone` and `employeeHireDate` as well; `IDirectoryReader` hands them over in `DirectoryProfile`. After each `DirectorySynced`, `DirectoryProfiles` updates directory values, the location and the reporting line |
| Contracts | `IReportingLine` (manager of, chain, direct and all reports, is manager of) and `IProfiles` (fields visible to a viewer) in `People.Contracts`; events `ProfileUpdated`, `ReportingLineChanged`, `LocationAdded` |
| API | `GET /api/v1/people` (search, rate limited per user), `/people/{id}`, `/people/{id}/org-chart`, `/people/newcomers`, `/people/locations`, `PUT /people/locations/{key}`, `GET/PUT /people/policy` (`If-Match`), `PUT /people/{id}/fields`, `PUT /people/{id}/manager`, `GET /me/team`, `GET/PUT /me/profile` |
| Access | `people.view` (Employee, HR officer, organization and tenant administrators), `people.hr_view` (HR officer), `people.manage` (organization and tenant administrators); migration 0010 adds them to existing tenants. Capabilities `people.directory` (every package) and `people.custom_fields` (Workplace Plus and up) |
| Tests | `PeopleDirectoryTests`, `PeopleSearchTextTests`, `PermissionBackfillTests` |

### Differences and follow-ups
| Item | Note |
|---|---|
| Same subtree | The spec does not define the subtree. It is the branch just below the top of the hierarchy that holds the person's primary unit, so an Acme Gulf person's field is visible across Acme Gulf and hidden from Acme Africa (section 7). |
| HR only rule | Section 6 gives employee number to "HR and administrators", which none of the four rules in section 2 covers, so a fifth rule `hr` exists for it. |
| Search on restricted fields | Search matches title, department, email and phones only while that field is visible to everyone; a field with a narrower rule is not used for matching at all, so a match can never reveal it. |
| Transliteration | Latin names also match on a consonant skeleton (Mohamed, Mohammed and Muhammad meet). Arabic to Latin matching needs a localized name set by an administrator. |
| Photos | Upload, resizing and the directory photo are not built: resizing needs an image library, which needs its own decision. Profiles show initials. |
| Cycle report | A reporting cycle is written to the audit log as `people.reporting_cycle_detected` and logged; no notification is sent yet. |
| Search analytics | `people.searched` is not recorded; there is no analytics module yet. |
| Policy cache | The policy is read on every request, so changes apply at once rather than within 60 seconds. |
| Settings audit | Newcomer period, opt-out and location attribute changes and location renames are audited as `people.settings_changed`, which section 13 does not list. |
| Performance | Search uses plain SQL over normalized columns with no extension. `PeopleSearchPerformanceTests` checks p95 under 300 ms at 50,000 members (98 ms locally). Evidence is in `specs/010-people-directory/evidence.md`. |
| Large teams of others | The org chart shows a count of further reports; the link to the full list exists only on your own profile (My team), since there is no reports list for another person yet. |
| Menus | The people pages are not in the global menu or quick navigation yet; the global menu comes from tenant configuration, and quick navigation entries come with the people component wiring (T008, T010). |
| Manual manager screen | Managers are set by hand through `PUT /people/{id}/manager`; the profile has no screen for it yet. |
| Tenant field keys | A new tenant field gets a generated key (`custom.f` and a time based suffix); administrators see only its names. |

## People directory pages (spec 010 T009)
| Part | Where |
|---|---|
| Pages | `/people` (search with unit and location filters, new joiners), `/people/{id}` (profile, contact actions from visible fields only, manager, org chart with chart and list views), `/people/team` (My team with indirect reports), `/people/me` (own profile, new joiner opt-out), `/admin/people` (field sources and visibility, tenant fields, settings, location names) |
| Code | `src/Web/features/people`, `src/Web/lib/people/types.ts`, the `people` message namespace; the admin section `people` is tenant-wide like notifications |
| Tests | `tests/Acceptance/specs/people.spec.ts` in English and Arabic with axe |

## People component (spec 010 T008)
| Part | Where |
|---|---|
| Manifest | `people.list` in `PeopleComponents` (category `people`, needs `people.directory` and `people.view`): source `newcomers`, `unit` (with its subtree) or `named`, and how many to show |
| Data | `GET /api/v1/people/newcomers`, and `GET /api/v1/people` with `unitId` or the new `ids` parameter (up to 50 ids; ids outside the tenant match no one) |
| Web | `components/placements/people.list.tsx`, registered in `lib/experience/registry.ts` |
| Tests | `PeopleDirectoryTests` (named set, bad ids), `PageCompositionTests` (picker), `people.spec.ts` (component on a published page, both languages, axe) |

| Item | Note |
|---|---|
| Choosing people | The builder has no unit or person picker for component properties yet, so the unit and the named people are entered as ids. A picker belongs to the builder (spec 032) and comes with a later builder task. |

## Network rules (spec 026 T001 to T005, T007)
| Part | Where |
|---|---|
| Module | `src/Modules/Security` and `Security.Contracts`; schema `security` with `policy`, `mfa_claim_mapping` and `alert` (forced RLS); `ISecurityPolicy` with a 30 second per-tenant cache cleared on commit of a change |
| Sessions | `identity.user_session` gains `source_ip`, `authenticated_at` and `last_seen_at` (migration 0005); `ISignInHistory` gives the addresses of recent sign-ins for the preview |
| Guard | `UseNetworkRules()` right after the session middleware, on tenant hosts only: blocklist, then tenant allowlist, then the admin allowlist for endpoints marked `InAdminArea()`. Refusal is 403 `security.network_not_allowed` with `area`, before any module or sign-in code runs. The address is `RemoteIpAddress` after the forwarded headers middleware, so with one trusted proxy hop only the rightmost `X-Forwarded-For` entry counts |
| Admin area | `InAdminArea()` on the admin, authz, audit, tenant, directory, identity provider, notification settings, HR, integrations, security and people administration endpoints |
| Endpoints | `GET` and `PUT /api/v1/security/policy` (ETag and If-Match), `GET /api/v1/security/policy/preview?ipAllow=&ipBlock=` |
| Keys | permissions `security.manage`, `security.view`, `security.respond` (backfilled by Authorization 0011); capabilities `security.ip_rules`, `security.session_policy`, `security.alerts` in every plan and `security.ip_rules_scoped`, `security.alerts_advanced` from Workplace Plus (Entitlements 0008) |
| Web | Server-side API calls forward the rightmost incoming `x-forwarded-for` so rules apply to the real caller |
| Tests | `NetworkRulesTests` (unit), `SecurityNetworkTests` (allow, block, admin area, spoofed header, lock-out confirmation, preview, plan limit, permissions, cross-tenant, audit, backfill) |

| Item | Note |
|---|---|
| Self lock-out | A change that refuses the caller's own address needs the address typed back, and the policy change itself needs step-up (T010). |
| Unknown routes | A path that matches no endpoint carries no admin area mark, so it gets the normal 404 rather than a network refusal; tenant rules still apply to it. |
| Audit rate | Refusals are audited once a minute per tenant, address and area, per instance. |
| Network zones (T006) | `network_zone` is a request attribute from `IRequestAttributeProvider` (the Security module's `NetworkZoneAttributes`), added to the current user's grants per request and never cached. It is multi-valued: an address in several zones satisfies `in` when any of them matches. The explainer runs for another user without their request, so a zone condition shows as a missing attribute there. |

## Session rules (spec 026 T011)
| Part | Where |
|---|---|
| Rules | `SessionRules` in Identity: the session middleware ends a session idle longer than `idleMinutes` or older than `absoluteHours` (reasons `idle_timeout`, `absolute_timeout`) and records use at most once a minute. These writes use their own short database session so they hold when the request changes nothing |
| Limit | With `maxSessions` set, a new sign-in ends the user's oldest sessions beyond the limit (`session_limit`) |
| Endpoints | `POST /api/v1/me/sessions/revoke-all` (sign out everywhere, clears the cookie), `POST /api/v1/identity/users/{id}/sessions/revoke-all` (`identity.users_manage`, admin area) |
| Tests | `SecuritySessionTests` |

| Item | Note |
|---|---|
| Lifetime | The existing 8 hour cookie and `expires_at` still apply, so the absolute timeout only shortens sessions until the cookie lifetime follows the policy. |
| Revocation speed | The middleware reads the session row on every request, so revoke-all takes effect on the next request, well inside the 1 minute of FR-003. |

## MFA assurance (spec 026 T009)
| Part | Where |
|---|---|
| Check | The Entra callback reads `amr` and `acr` and asks `IAuthenticationAssurance` (Security) whether they show MFA under the tenant mapping for provider `entra` (default `amr` contains `mfa`). A sign-in that shows MFA sets `seen_at`, which lets the tenant turn on `requireMfa` |
| Refusal | With `requireMfa` on, a sign-in without the claim creates no account or session, goes to `/signin/problem?reason=mfa_required` and is audited as `security.mfa_missing` |
| Tests | `EntraSignInTests` (MFA case); the fake Entra ID sends `amr` (default `pwd`) |

| Item | Note |
|---|---|
| Claim mapping | There is no screen to change the mapping yet; the default fits Entra ID. The admin pages come with T016. |

## Step-up (spec 026 T008, T010)
| Part | Where |
|---|---|
| Marking | `RequireStepUp()` endpoint metadata on the section 6 actions that exist: `PUT /security/policy`, `POST` and `DELETE /authz/assignments`, `PUT /tenant/identity-provider`, `PUT /tenant/directory`, `POST /audit/exports`, `POST /integrations/connections`, `POST /tenant/admins` |
| Guard | `UseStepUp()` after the network guard: when the session's `authenticated_at` is older than `stepUpMinutes` (default 10), the answer is 401 `security.step_up_required` with `stepUpUrl` and `stepUpMinutes`. Development sign-in sessions count as fresh |
| Fresh sign-in | `GET /api/v1/auth/signin/sso?stepUp=true&returnTo=` sends `prompt=login` and `max_age=0`; the callback needs an `auth_time` within 5 minutes, then audits `security.step_up_completed`, starts a new session and ends the old one (`stepped_up`). A stale `auth_time` is audited as `security.step_up_failed` |
| Tests | `EntraSignInTests` (every marked action fresh, stale and after step-up; stale provider session; shorter window) |

| Item | Note |
|---|---|
| Web handling | No web page calls a marked action yet. The security policy page (T016) is the first; it sends the user to `stepUpUrl` with `returnTo` when it gets `security.step_up_required`. |
| Admin roles | Section 6 names admin roles; every role assignment and removal is marked, the stricter reading, because role definitions can make any role an admin role. |
| Providers without forced re-authentication | Entra ID honours `prompt=login` and `max_age`, so the setup warning of US2 scenario 3 is not needed for the only R1 provider. |

## Security alerts (spec 026 T012, T013)
| Part | Where |
|---|---|
| Rules | `DownloadVolumeRule` on `DocumentDownloaded` (counts per user per hour in `security.download_count`, default 200 files or 2 GB); `OutOfHoursGrantRule` on `RoleAssignmentChanged` for roles holding a permission in `Permissions.Privileged`, outside the tenant default calendar (spec 022) and 08:00 to 18:00 (`security.alerts_advanced`); the network guard raises `network_blocked` once per address, area and hour |
| Store | `security.alert` with a dedupe key, so a rule raises one alert per subject and window and later events update its facts. Raising is audited as `security.alert_raised` and publishes `SecurityAlertRaised` |
| Endpoints | `GET /api/v1/security/alerts?status=` (`security.view` or `security.manage`), `POST /api/v1/security/alerts/{id}/handle {outcome, note}` (`security.respond`, audited as `security.alert_handled`) |
| Contracts | New `Documents.Contracts` with `DocumentDownloaded` (IDs and size only), ahead of spec 011 downloads; `IAuthorizationProvisioning.RolePermissionsAsync` |
| Tests | `SecurityAlertTests`, `AlertSettingsTests` |

| Item | Note |
|---|---|
| Thresholds | Thresholds and working hours live in `security.policy.alert_settings`; there is no API to change them yet. Editing them is a `security.alerts_advanced` feature and comes with the alerts page. |
| Privileged permissions | Spec 026 says "admin role" without a list. The smallest safe reading is a role that holds any permission that changes access, sign-in or tenant configuration (`Permissions.Privileged`). |
| First administrator | The tenant's first administrator is assigned by the system at creation and, at night, raises an out-of-hours alert like any other grant. |

## Security posture (spec 026 T014)
| Part | Where |
|---|---|
| Endpoint | `GET /api/v1/security/posture` (`security.view` or `security.manage`) returns `passed`, `total` and `checks[] {key, state, link}`; the state is `passed`, `failed` or `unavailable` |
| Checks | `sso_configured`, `mfa_required`, `ip_rules` (an allowlist is set), `admin_area_rule` (unavailable without `security.ip_rules_scoped`), `step_up_window` (10 minutes or less), `idle_timeout` (30 minutes or less), `absolute_timeout` (12 hours or less), `dev_sign_in_absent` |
| Contracts | `ISignInMethods` in Identity.Contracts says whether an enabled SSO provider exists and whether development sign-in is on |
| Tests | `SecurityPostureTests`, `PostureTests` |

| Item | Note |
|---|---|
| Score | The spec gives no formula, so the page shows passed checks out of the checks that apply (plan, Posture). Unavailable checks do not count. |
| Recommended values | The spec 026 defaults are taken as the recommended baseline. |
| Development sign-in | Has no link, because the host configuration fixes it, not the tenant administrator. On staging it shows as failed, which is correct. |

## Secure rendering (spec 026 T015)
| Part | Where |
|---|---|
| Policy | `src/Web/proxy.ts` sets a content security policy on every page: `script-src 'self' 'nonce-…' 'strict-dynamic'` with a new nonce per request, `object-src 'none'`, `base-uri 'self'`, `frame-ancestors 'self'`, and everything else on the tenant host. Next.js puts the nonce on its own scripts. `'unsafe-eval'` is added on the development server only |
| Tests | `SecureRenderingTests` (architecture: the policy has a nonce and no `unsafe-inline` for scripts; the web code never writes markup from data; property types have no raw markup type and text and URLs are checked), e2e `secure-rendering.spec.ts`, and every e2e test now fails on a policy violation |

| Item | Note |
|---|---|
| Inline styles | `style-src` allows `'unsafe-inline'` because React sets style attributes for layout values. FR-025 asks for no inline script; styles cannot run code. |
| Rich text | No rich text property exists in R1: text is plain and refused when it holds markup. The first rich text type must bring a server-side sanitizer, and the architecture test names that rule. |
| Embeds | No embed component exists yet, so `frame-src` is `'self'` (the page builder preview). The tenant embed allowlist of spec 032 extends it when embeds arrive. |

## Security pages (spec 026 T016)
| Part | Where |
|---|---|
| Pages | `/admin/security` (policy with the lock-out preview, MFA and session rules), `/admin/security/alerts` (filter, handle or dismiss with a note), `/admin/security/posture`, in English and Arabic |
| Admin shell | New `security` section on `security.view`, tenant-wide only. The navigation answer carries `badge`, the open alert count, from the new `ISecurityAlertSummary` contract |
| Step-up | Saving the policy on an old sign-in gets `security.step_up_required`; the page sends the user to `stepUpUrl` with `returnTo=/admin/security` |
| Notifications | `AlertNotifier` handles `SecurityAlertRaised` and tells holders of `security.respond` tenant-wide (new `IAuthorizationProvisioning.TenantHoldersAsync`) in the mandatory `security_notice` category, one template per alert kind, linking to the alerts page. The text names the kind only |
| Alert names | Alerts carry `subjectName` and `handledByName` from the user directory |
| Tests | `SecurityAlertTests` (notification and badge), e2e `security.spec.ts` in both languages with axe |

| Item | Note |
|---|---|
| Network zones | Zones (T006) are set through the API only; the policy page does not edit them yet. |
| Alert thresholds | Still no screen or API to change thresholds and working hours (`security.alerts_advanced`). |
| Recipients | Holders are read from tenant-scope assignments, direct or through a group; assignment conditions are not evaluated for who is told, because the notification shows no facts. |
| Policy editor for readers | Security officers see the same form; the API refuses their save with its own message, as the screen does not check roles. |

## Process engine core (spec 024 T001 to T006)
| Part | Where |
|---|---|
| Module | `Workflow` and `Workflow.Contracts`, schema `workflow` with forced RLS on definitions, instances, user tasks, engine jobs and history |
| Routes | `RouteSpec` (steps in order, each with named users or a permission, an optional due time in working days, and starter exclusion) compiles to BPMN 2.0 XML with the `teambeats` namespace and to an executable graph. `RouteCompiler.FromBpmn` reads the XML back and refuses anything outside the R1 subset |
| Contract | `IProcessEngine`: publish (immutable versions; the same route again adds no version), start (one instance per business key), complete (candidate check, 409 once closed), withdraw (the starter only), queries, history, and the open tasks of the current user |
| Candidates | A named user, or a holder of the step's permission at the instance's org unit through central authorization, so a holder in a sibling unit is not a candidate. The starter is never a candidate when the route excludes them |
| Events | `UserTaskCreated`, `UserTaskCompleted`, `ProcessInstanceCompleted`, `ProcessInstanceWithdrawn`, `UserTaskDue`. Audit: `workflow.definition_published`, `workflow.task_completed`, `workflow.instance_withdrawn` |
| Due times | `EngineJobs` computes the due time from `IWorkingCalendar` for the instance's unit and stores a `task_due` job. `EngineJobWorker` runs due jobs per active tenant every 10 seconds (worker host; development host with `Workflow:Jobs:InProcess`) and deletes done jobs after 30 days. `WorkingCalendarChanged` recalculates open due times and moves their jobs |
| Tests | `RouteCompilerTests`, `WorkflowEngineTests`, `WorkflowDueTests` |

| Item | Note |
|---|---|
| Role at unit | FR-006 names an org unit role. The hard rules ban role names in checks, so a step names a permission that the role holds (for example `workflow.approve` from the approver role), checked at the instance's unit. |
| Due time of day | The spec gives working days without a time of day. The smallest safe default is the end of the Nth working day (local midnight after it) in the calendar's time zone, counted from the day the task was created. |
| Jobs | Jobs are claimed with `for update skip locked` in the worker's transaction, and a job is marked done before its effect, so a second delivery does nothing. A reminder only fires while the task is open and its due time still matches. |
| Recalculation | Every open task with a pending reminder in the tenant is recalculated on a calendar change, because a change reaches units through inheritance. Reminders already sent are not sent again. |
| Delegation (T007) | Needs the delegation contract of spec 016 and lands with it: the engine will ask that contract for active delegates when it lists or completes tasks, and write "on behalf of" in history. |
| Line manager candidates | Line manager and manager chain candidates (FR-006) wait for the routes of spec 016 that need them. |

## Service catalogue (spec 016 T001 to T005)
| Part | Where |
|---|---|
| Module | `Services` and `Services.Contracts`, schema `services` with forced RLS on categories, services, versions, requests, Tracking events, attachments, favourites, recent use and delegations |
| Catalogue | `GET /api/v1/services?q=&category=` returns categories, the services offered to the viewer and their recent use; `GET /api/v1/services/{id}` returns the published form, or 403 `services.not_available` when the audience leaves the viewer out; favourites and `POST /{id}/used` |
| Builder | `/api/v1/services/admin`: create a form service or linked app, settings, draft form and approval step, publish, retire, and categories. Owners need `service.manage` at the owning unit, or the tenant for services without one |
| Forms | `FormValidator` checks definitions on save and data on submit: the field types of FR-003, required, show-when conditions on earlier choice fields, comparisons with constants and with today plus or minus days. Prefill sources are a fixed list |
| Module services | Modules register `IModuleServiceSource`; their services join each tenant's catalogue on first read, for everyone, and an administrator can then set the audience and category |
| Permissions and plan | `service.manage` (tenant and organization administrators, workflow designers), `service.request_on_behalf` (administrators); `workflow.catalogue` in every package; `workflow.max_services` quota |
| Preview | `GET /api/v1/services/admin/{id}/preview` returns the latest version, draft or published, with the form prefilled for the owner, so the builder shows it as a requester will see it. Nothing is stored |
| Tests | `FormValidatorTests`, `ServiceCatalogueTests` |

| Item | Note |
|---|---|
| Quota values | The spec leaves the service quota to commercial data. Defaults: 25 on Workplace, 200 on Workplace Plus (the design-to scale), unlimited on Enterprise and Government. Module services do not count. |
| Audience | Every criterion that is given must match, and inside a criterion any value matches, as in the spec 008 audience rules. Org units cover their subtree. No criterion means everyone. A service owned by a unit must name units inside it. |
| Approver rule "role at unit" | Stored as a permission held at the requester's unit, because checks never use role names. Line manager approval comes with T016. |
| Lookup lists | A lookup field keeps its options in the form in R1. Shared tenant lookup lists can come later without changing submitted data. |
| Rule messages | Errors carry resource keys. The owner's own message per locale is stored with the rule and shown by the form. |
| Search | The catalogue filters names, descriptions and synonyms in every locale, folding Arabic letter forms. Global search (spec 012) indexes services from `ServicePublished`. |

## Requests and attachments (spec 016 T006 to T008)
| Part | Where |
|---|---|
| Prefill | `GET /api/v1/services/{id}/prefill?for=` returns the values for the person the request is for and the keys that stay read only. Account, manager and org unit values are always read only; a profile value is read only when its field comes from the directory. Submit puts the read only values back on the server, whatever the client sent |
| Submit | `POST /api/v1/requests` validates against the published version, takes today from the working calendar of the person it is for, numbers requests `REQ-yyyy-000001` per tenant, and starts the approval route. A repeated idempotency key returns the first request |
| On behalf | Needs the service to allow it and `service.request_on_behalf` at the primary unit of the person it is for. The filer and that person are never candidates of its steps |
| Reading | `GET /api/v1/requests` lists requests filed by or for the caller with status, current step and assignees. `GET /api/v1/requests/{id}` adds the data, attachments and Tracking. The filer, the person it is for, a step candidate or completer, and the service owner may read it; anyone else gets 404 |
| Cancel | `POST /api/v1/requests/{id}/cancel` by the filer while it is open; the open task is cancelled with the instance |
| Attachments | `POST /api/v1/requests/attachments?name=` takes the raw file up to 10 MB, scans it through `IMalwareScanner` before storing it under `tenants/{tenant}/services/attachments/`, and refuses an infected file. A submit only links the caller's own clean uploads. Download goes through the request's read check, returns 409 until clean, and serves only safe types inline with `nosniff` and a sandbox policy |
| Scanner | `ClamAvScanner` (INSTREAM) when `Scanning:ClamAv:Host` is set, a `clamav` service in compose; without it every scan fails closed. Tests and the dev host use a signature scanner that flags the EICAR test file |
| Tests | `ServiceRequestTests` |

| Item | Note |
|---|---|
| Staging scanner | Staging runs the dev host, which uses the signature scanner because there is no ClamAV machine yet. A production deployment without `Scanning:ClamAv:Host` fails every scan, so nothing unscanned is attached. A ClamAV Fly machine is the follow up. |
| Who cancels | The spec names the requester. Only the filer cancels; the person a request was filed for can read it but not cancel it. |
| Read only prefill | The spec says directory values stay read only. Fields an employee may edit in their own profile stay editable in the form. |

## Task inbox and delegation (spec 016 T009 to T012, spec 024 T007)
| Part | Where |
|---|---|
| Inbox | `GET /api/v1/tasks?service=` lists every open engine task the caller may act on now, oldest first, with the request number, service, step names per locale, requester, the person it is for, "on behalf of" and due time. `GET /api/v1/tasks/count` feeds the header badge |
| Decisions | `POST /api/v1/tasks/{id}/decision` with `approve`, `reject` (reason required) or `return`. It needs a current assignment and `workflow.approve` at the request's unit. Another tenant's task is not found; a task of the tenant the caller is not assigned to is refused |
| Request update | The decision moves the request in the same transaction: approved and rejected close it and publish `RequestCompleted`, returned keeps it open for the filer and publishes `RequestReturned`. Each decision is a Tracking entry with actor, on behalf of and comment, and audited as `task.approved`, `task.rejected` or `task.returned` |
| Resubmit | `POST /api/v1/requests/{id}/resubmit` by the filer of a returned request, on the same form version, with the same validation and read only prefill. The route starts again under business key `{request}.{round}`, so each round has its own history; audited as `request.resubmitted` |
| Delegation | `GET`, `PUT` and `DELETE /api/v1/me/delegation`: one delegate for a range of days on the delegator's calendar, at most a year. The engine asks `IDelegations` (Workflow.Contracts, kept by Services) when it checks a candidate, so tasks reach the delegate inside the range and come back after it without moving anything. The engine stores "on behalf of" on the task and in history. Audited as `delegation.set`, `delegation.changed` and `delegation.ended` |
| Authorization | `IAuthorizationProvisioning.UserHoldsAsync` checks another person's grants, for a delegate acting on behalf of an approver who holds a permission at the unit |
| Tests | `TaskInboxTests` |

| Item | Note |
|---|---|
| Approve permission for delegates | The spec asks for `workflow.approve` besides the assignment. A delegate acts with the authority of the person they stand in for, so the check uses that person's grants. A delegate who is excluded from the request (filer or the person it is for) is never a candidate, so the step stays with the approver. |
| The delegator during the window | The delegator still sees the task, and may still act on it. The spec says the delegator can still see it and is silent on acting; refusing them would stall urgent approvals. |
| Assigned without the permission | A named approver without `workflow.approve` at the unit does not see the task and cannot act. Tenants give named approvers the Approver role; a manager holds the approval for their reports through the reporting line (see Line manager approvals). |
| Return reason | The spec requires a reason to reject only, so returning takes an optional comment. |
| Route version on resubmit | The form stays at the request's version; the route is the latest published version of the service. |
| Other sources | Tasks from other engine sources (content, policy and document routes later in R1) appear in the same inbox with their definition key as source. Their own modules will add request style details when they are built. |

## Line manager approvals and Team requests (spec 016 T016)
| Part | Where |
|---|---|
| Rule | The approver kind `line_manager` publishes a step with `CandidateRule(LineManager: true)` and a fallback permission (`workflow.approve` unless the owner names another). BPMN carries it as `<teambeats:lineManager/>` |
| Subject | The engine stores `requested_for` on the instance (migration `workflow/0004`): the person a request is for, or the starter. A line manager step goes to that person's current manager from `IReportingLine` (People.Contracts) |
| Resolution | The manager is read when a candidate is checked, not when the task is created, so a manager change during approval moves an open step to the new manager. With no manager, or when the manager is excluded (they filed the request for their report), the step goes to holders of the fallback permission at the requester's unit |
| Approval right | Spec 016 says a line manager holds `workflow.approve` for their reporting line. The inbox and decision check accept a manager anywhere in the subject's chain as holding it, so a manager and their delegate act without an Approver role |
| Team requests | `GET /api/v1/requests/team?status=` lists requests for the caller's direct and indirect reports by the reporting line. A manager in the chain may read those requests. Org unit membership never adds anyone. Page `/requests/team` |
| Notifications | `RequestNotifier` tells the filer and the person a request is for when it is approved, rejected or returned (`request_update`), and tells a named approver or line manager that a request waits (`approval_waiting`). Notifications carry the request number only |
| Tests | `LineManagerTests`, `RouteCompilerTests`, `services.spec.ts` (Team requests) |

| Item | Note |
|---|---|
| Team requests subject | A request counts for the person it is for. A request a report files for someone outside the team is not a team request. |
| Fallback alert | The spec asks to alert the service owner when a step falls back. The fallback works; the owner alert waits for service level alerts (US8, R2), which add the owner as a recipient. |
| Approvers by permission | Holders of a permission at a unit are not told one by one, because finding every holder at a unit needs a reverse grant query the authorization service does not offer yet. They see the header badge and the inbox. |
| Manager chain level | Only the direct manager is assigned. The "manager of manager" and chain level rules arrive with multi-step routes (US5, R2). |

## Service pages (spec 016 T015)
| Part | Where |
|---|---|
| Pages | `/services` catalogue with search, categories, favourites and recent use; `/services/{id}` request form; `/requests` My requests with a status filter; `/requests/{id}` Details and Tracking with cancel and, from the inbox, the decision panel; `/requests/{id}/edit` to change and submit a returned request; `/tasks` inbox and delegation; `/admin/services` and `/admin/services/{id}` for owners |
| Shell | The `task_badge` region shows the open task count from `GET /api/v1/tasks/count`, read every 30 seconds while the page is visible, like the notification bell |
| Admin | The `services` admin section appears for holders of `service.manage` anywhere; the API scopes the list and every change to the owner's units |
| Forms | One renderer for the request form, the resubmit form and the builder preview. It shows and hides fields on their conditions; every rule, read only value and permission is checked again by the API |
| Tests | `tests/Acceptance/specs/services.spec.ts`: build, preview and publish; find, file and follow; badge, inbox and decide; approved in Tracking; in English and Arabic with axe |

| Item | Note |
|---|---|
| Person fields | A person field holds one colleague, picked through the people directory search, so it only holds IDs the API returned. |
| Lookup and service templates | The builder keeps options inside the field (see the catalogue notes); service templates arrive with R2 (US7 scenario 1 "from a template"). |
| Fulfilment queue | Queues and fulfilment (US5) are R2, so the builder does not show the queue setting yet. |

## Content items and lifecycle (spec 009 T001 to T004, T006, T016, T017)
| Part | Where |
|---|---|
| Module | `src/Modules/Content` and `Content.Contracts`, schema `content` with forced RLS. Events `ContentPublished`, `ContentUnpublished`, `ContentExpired` |
| Permissions | `content.author` (own drafts), `content.edit` (any draft of the site), `content.publish`, `content.publish_critical`, `content.admin` (sites and settings), all checked at the site's org unit through central authorization. Migration `authz 0013` grants them to the built-in roles of every tenant |
| Packages | `content.core`, `content.critical_ack`, `content.multistep_approval`, `content.max_sites` (3, 25, unlimited), `content.campaigns`, `content.ai_translation`, `ai.authoring` (migration `entitlements 0010`) |
| Body | A block document (paragraph, heading 2 to 4, list, quote, table, image by asset id, link, embed) with bold and italic marks, never HTML. Links are https, http, mailto or tenant paths; embeds are https on a host of the tenant allowlist or its subdomains. Limits: 500 blocks, 100,000 characters |
| Audience | Clauses joined by OR, criteria inside a clause by AND: org units with or without subtree, locations from the people profile (spec 010), directory groups, roles and membership types |
| Lifecycle | Draft, scheduled, published, expired, archived; `in_review` and `approved` are stored for the approval route of the next slice. Each move is audited. A published version is immutable in the database (trigger). Editing a live item starts the next version and the live one stays until that is published, also when the next version is scheduled |
| Scheduler | `ContentScheduler` runs once a minute per active tenant from the worker (`ContentScheduleWorker`) and from the development host (`Content:Schedule:InProcess`): due versions go live, live items past their expiry expire, expired items archive after 30 days |
| API | `/api/v1/content/sites`, `/api/v1/content/settings`, `/api/v1/content/items` with `publish`, `unpublish`, `archive`, `view` (reader) and `versions` |
| Tests | `ContentRulesTests` (unit), `ContentLifecycleTests` (integration) |

| Item | Note |
|---|---|
| Content types | The three built-in types (news, announcement, page) and their defaults live in code; a tenant changes the settings per type in `content.content_type`. Custom types come later. |
| Sites | Sites are created and changed with `content.admin`. The per-site management permission and site homes come with US6 in R2. |
| Locations | Locations are now targetable, because the people profile of spec 010 has a location. Profile attributes and channels are stored in the model but refused until their releases. |
| Schedule times | Publish and expiry times are entered as local times in the site's time zone and stored in UTC. |
| Direct publish | Until the approval route of the next slice, a publisher publishes directly and is recorded as the version's approver. |
| Unpublish | Unpublish returns an item to draft with its versions, so it can be published again. |
| Reading | Readers need `content.view` on the item as a resource: public and internal items have tenant visibility, so every active member may read them; confidential and restricted items need a role grant covering the site's unit or an entry on the item. No role grants `content.view` to everyone, which keeps restricted resources closed. |

## Content approval and feed (spec 009 T005, T007, T008)
| Part | Where |
|---|---|
| Routes | A site keeps a route per content type: none (publishers publish directly) or one review step with named approvers. With a route, a draft goes live only through `POST /api/v1/content/items/{id}/submit`; `publish` is refused with `conflict.content.approval_required` |
| Engine | Each site, type and set of deciders compiles to its own engine definition `content.{site}.{type}.{hash}` (source `content_route`), so a change of approvers applies to the next submission and running reviews keep their route. The business key starts with the item ID |
| Deciders | The named approvers other than the writers (the person who submits, the version's author and the item's creator), or the site's publishers (`content.publish` at the site's unit) when that leaves nobody (US1 scenario 6). The engine excludes the writers and the content module checks again before a decision |
| Inbox | The spec 016 inbox serves tasks of other modules through `ITaskSource` (Workflow contracts): the module describes the task (title per locale, link, unit, requester), says who may decide instead of `workflow.approve`, and moves its object on after the decision in the same transaction |
| Decisions | Approve releases the version at the wanted time, or now when that time has passed (event `schedule_missed`, the person who submitted is told). Return and reject send the draft back with the comment. A live item stays published and readable while its next version is reviewed. `withdraw` takes a draft out of review. `GET .../reviews` shows the history |
| Feed | `GET /api/v1/content/feed?site=&type=&cursor=&limit=` returns live items whose audience includes the viewer and which the viewer may read, pinned first and then newest, with an opaque cursor |
| Tests | `ContentReviewTests` |

| Item | Note |
|---|---|
| Default route | Sites start without routes, so publishers publish directly until a communications administrator adds one (FR-003 allows routes "from none"). |
| Reject | Reject and return both send the draft back to its writers; the spec names only return. |
| Feed scan | The feed reads live items in batches and checks audience and access per item, at most 1,000 per page request. The performance targets of section 15 are checked with T024. |
| Inbox link | The inbox shows a content review with its title; the link opens once the item editor pages of T023 exist. |


## Critical announcements (spec 009 T009 to T011)
| Part | Where |
|---|---|
| Settings | `critical` on an announcement: `modal` or `banner`, require acknowledgement (always on for a modal), "remind me later" count (default 3, at most 10), a deadline as a local time in the site's zone, and dismissible (banners only). `"presentation": "none"` clears them. Setting them needs the capability `content.critical_ack` |
| Sending | Publish and submit of a critical item need `content.publish_critical` at the site, and at tenant scope when the audience reaches past the site's units. A refusal is audited as `content.critical_denied` with outcome denied and the request fails with 403 |
| Audience | On `ContentPublished` the module fills `content.audience_member` with every active member the audience matches and audits `content.critical_sent` with the audience size. `OrganizationMembershipChanged` adds or removes that person on every live critical item. A material version resets "remind me later" counts and dismissals and notifies the audience (category critical announcement) |
| Shell check | `GET /api/v1/content/critical/pending` returns at most one modal and the banners for the signed-in person, in their language, leaving out items they may not read, items confirmed from the counted version on, dismissed banners and modals put off |
| Actions | `POST .../items/{id}/acknowledge` (`locale`, `channel` web or mobile) records user, version, time, channel and SHA-256 of title, summary and plain body text; insert only. `POST .../remind-later` puts a modal off for 4 hours up to the allowed count. `POST .../dismiss` hides a dismissible banner |
| Counting | Confirmations count from the latest published version marked material; minor versions keep them (section 6) |
| Deadline | The content scheduler, once the deadline passes, notifies each person who has not confirmed and each of their line managers once with the count. `GET .../acknowledgements/team` lists a manager's direct reports in the audience with their status |
| Completion | `GET .../acknowledgements` gives counts; `POST .../ack-report` (202) audits `content.ack_report_exported` and the worker writes a CSV (person, name, email, status, time, channel, version) to tenant storage for 7 days: `GET /api/v1/content/ack-reports/{id}` and `/file`. Both need `content.publish_critical` at the site or `content.admin` |
| Tests | `ContentCriticalTests` |

| Item | Note |
|---|---|
| Submit | A critical item sent through an approval route needs `content.publish_critical` of the person who submits; the approver then releases it. The spec names the permission for sending only. |
| Recall, rate limit, second approver | Section 9 asks for a recall action, a rate limit on critical sends and an optional second approver per tenant. Unpublish already removes the modal and banner; the rest waits for the tenant setting and is recorded as a gap. |
| Manager list | Managers see their direct reports only, through the spec 010 reporting line; the relationship, not a role, gives the access. |
| Links | Notifications link to `/content/read/{id}` and `/content/items/{id}/team`; those pages come with T023, as does the shell's modal and banner component. |
| Audience size | Refreshing the audience evaluates every candidate member's facts; when every clause names org units only their members are evaluated. The 2 minute target for 100,000 people is checked with T024. |

## Governance, history and restore (spec 009 T012 to T015)
| Part | Where |
|---|---|
| Review task | The content scheduler opens the owner's review 30 days before `review_due_at` on live items: one engine step for the owner (`content.review.{owner}`), shown in the spec 016 inbox as source `content_owner_review`, with `ContentReviewDue` published and the owner notified |
| Actions | `POST .../items/{id}/owner-review` with `confirm`, `edit` or `retire` (and an optional `nextReviewOn`). In the inbox, approve means confirm, return means edit and reject means retire. Confirm and edit set the next review date (the type's review period, 12 months when it has none); edit also copies the live version into a new draft. Audited as `content.reviewed` |
| Retire | An owner holding `content.publish` archives at once. Anyone else starts a one-step retirement for the site's publishers (`content.retire.{site}`, source `content_retire`); approval archives the item |
| Overdue | Once the review date passes, the owner's line manager and the communications administrators are told once per date, and readers see `underReview` on the reader view when the type has the label |
| Owner leaves | `UserDeprovisioned` hands the person's items to an active backup owner; otherwise the item becomes ownerless and the administrators are told. An open review task is withdrawn. Audited as `content.owner_reassigned` per item |
| Health | `GET /api/v1/content/health` counts per site items overdue for review, without an active owner and expired; `GET /api/v1/content/health/items?site=&kind=overdue|ownerless|expired` lists them. Needs `content.admin` |
| History | `GET .../versions/{n}` gives one version with its text; `GET .../compare?from=&to=&locale=` returns kept, added and removed runs of title, summary and body text, word by word; `POST .../versions/{n}/restore` makes the next draft from it (`restoredFrom`), audited as `content.version_restored`. Readers see none of it unless the type shows history to readers |
| Tests | `ContentGovernanceTests` |

| Item | Note |
|---|---|
| Site owner | Sites have no owner of their own in R1 (`site.manage` comes with R2 sites), so the communications administrators (`content.admin` at tenant scope) stand in for the site owner in overdue and ownerless notices. Without a backup owner an item becomes ownerless rather than going to a site owner. |
| Edit date | Choosing edit also moves the review date on, so the next reminder does not open while the update is being written. |
| Retire in the inbox | Reject in the inbox asks for a reason, so retiring from the inbox needs a comment; the item page action does not. |
| Not viewed | The not viewed count waits for spec 021 view counts and is returned as null. Expired counts expired items; "still linked" needs page placements from the Experience module and comes with the page builder components of T019. |
| Restore with a draft | Restore is refused while a draft exists, so a draft in progress is never overwritten. |

## Page components, reactions and comments (spec 009 T018 to T021)
| Part | Where |
|---|---|
| Components | `ContentComponents` registers five spec 032 manifests: `content.news_feed`, `content.rich_text`, `content.links`, `content.call_to_action` and `content.announcement_banner` (one per page, also needs `content.critical_ack`). All need `content.core`, so a tenant without it is not offered them and the content APIs refuse them. The web parts live in `components/placements/content.*.tsx` and read the content APIs with the viewer's session on every render |
| Reader | `/content/read/{id}` shows a live item with its body blocks, the language fallback, expired and under review labels, then reactions and comments |
| Reactions | `GET` and `POST .../items/{id}/reactions` with `like`, `celebrate`, `support` or `insightful`; one per person and item, `none` takes it back |
| Comments | `GET` and `POST .../items/{id}/comments`, then `POST /api/v1/content/comments/{id}/approve`, `/hide`, `/report` and `DELETE /api/v1/content/comments/{id}`. A visible comment notifies the item owner, or its creator when it has none (`content.comment.new`). With site moderation on, comments from people without `content.edit` at the site wait for a moderator. Moderation is audited as `content.comment_moderated` |
| Tests | `ContentSocialTests`, `ExperienceValidationTests` |

| Item | Note |
|---|---|
| Report threshold | Three reports from different people hide a visible comment until a moderator looks at it. The number is a constant (`ContentSocial.ReportsToHide`), not a tenant setting, until the spec asks for one. Approving a reported comment starts the count again. |
| Moderators | A moderator is anyone holding `content.edit` at the site, since the spec names no separate moderator permission. |
| Content images | Image blocks show their alt text as a caption until spec 011 stores content media; embeds use a sandboxed frame from the tenant allowlist. |
| Mentions and photos | Mentions, hashtags and photos in comments are R2 (US9 R2 part). |
| Still linked | The health count of expired items that pages still show needs a placements query from the Experience module and is left for slice 6. |

## Block editor and item editor (spec 009 T022, T023 part)
| Part | Where |
|---|---|
| Editor | `src/Web/features/content/editor`: Lexical (MIT, the one new dependency the 009 plan lists) with paragraphs, two heading levels, quotes, bulleted and numbered lists, bold, italic, links, undo and redo. `convert.ts` reads and writes the body document, never HTML. Embeds, images, tables and link cards are kept as cards the author can remove (`AtomicBlockNode`), so they round trip unchanged. Each block takes its direction from its first strong character, so Arabic and English mix in one body |
| Pages | `/content/items` (filter by site, status and type), `/content/items/new`, `/content/items/{id}` with a tab per language, owner and backup owner, review date, classification, pin and feature, audience (everyone, or units with or without the units below them and membership types), critical options for announcements, publish now or at a site time with an expiry, send for approval, withdraw, unpublish, archive, and a preview at desktop or phone width |
| Tests | `tests/Acceptance/specs/content-authoring.spec.ts` in English and Arabic with axe |

| Item | Note |
|---|---|
| Nested lists | The body document has one list level, so a nested list's text is kept in its parent item on save. |
| Other formats | Lexical shortcuts for underline or code are dropped on save, since the body document keeps bold and italic only. |
| Audience preview | The preview names the chosen units and membership types; counting the people an audience reaches needs a directory query and is left for the analytics of spec 021. |
| Groups and locations | The audience picker offers units and membership types. Groups and locations are accepted by the API and come to the picker with the directory groups screen. |

## History, confirmations, health and the shell notices (spec 009 T023 rest)
| Part | Where |
|---|---|
| History | `/content/items/{id}/history`: versions with writer, approver, note and date; compare any two in one language word by word (added text underlined, removed struck through, so it reads without color); restore an older version into a new draft |
| Confirmations | `/content/items/{id}/acknowledgements` for publishers (audience, confirmed, pending, by channel, the counted version and deadline, and the completion report with a download once it is built); `/content/items/{id}/team` for managers, the page the deadline notice links to |
| Health | `/content/health`: per site, items overdue for review, without an active owner and expired, each count opening the list of its items |
| Shell | `CriticalNotices` in the shell header shows the pop-up (a modal dialog that Escape does not close, since it always needs an answer) and the banners above the content of every page, as US2 scenario 6 asks |
| Development host | `Messaging:Outbox:InProcess` lets the development host dispatch the outbox itself, so local runs and the e2e suite see background steps (audiences, notices) without the workers host. Staging leaves it off because its workers app dispatches |
| Tests | `tests/Acceptance/specs/content-governance.spec.ts` in English and Arabic with axe |

| Item | Note |
|---|---|
| Banner component | The shell shows banners on every page, so the `content.announcement_banner` component renders nothing when the shell strip is on the page and only shows them on surfaces without the shell. |
| Pop-up answers | A pop-up always asks for confirmation (the API sets this for modal presentation), so the pop-up offers confirm and remind me later only. |
