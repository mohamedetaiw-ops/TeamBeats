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
| Telemetry `integration.graph_call` and OpenAPI examples (T018) | Not built yet. |

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
| Tenant templates, distribution and layout change (T010, T011) | Not built. Pages use the platform templates only. |
| Manifest migration hooks (part of T012) | Not built. Only one version of each reference component exists. |
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
