# Feature Specification: Public API, MCP Server and App Access

| Field | Value |
|---|---|
| Spec ID | 035-public-api-and-mcp-server |
| Area | integrations |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R2 (tenant authorization server with OAuth 2.1, app registrations for application and delegated access, scope catalogue tied to permissions and entitlements, public REST API v1 with its own OpenAPI document, rate limits and monthly quotas, event subscriptions over signed webhooks, remote MCP server with read tools, resources and confirmed write tools for external AI clients, external AI client policy, connected apps page for employees, usage and audit), R3 (sender-constrained tokens with DPoP or mutual TLS, admin APIs beside SCIM, confirmation inside the AI client through MCP elicitation, MCP prompts, developer sandbox tenant, public app gallery) |
| Last updated | 2026-11-08 |
| Approved | 2026-11-08 by Mohamed (standing approval to proceed) |
| Requirement IDs | API-001 to API-030 (new); foundation section 8 principle 8 (open standards), C-07 (API contracts) |
| Use cases | UC-07 (actions through other systems), UC-10 (integration) |
| Depends on | 003 (sign-in through the tenant identity provider), 004 (authorization, principals, batch evaluation), 005 (entitlements and quotas), 006 (admin shell, audit), 013 (AI Gateway, for the ask tool only), 014 (assistant answers), 015 (confirmation notices), 016 (task inbox), 017 (outbound webhook runtime), 020 and 027 (tool registry, tool gateway, execution records, emergency controls), 026 (IP rules, alerts, SIEM), ADR-0018 |
| Used by | Customer integrations, third-party apps, external AI clients and agents (for example Claude, ChatGPT, Microsoft Copilot Studio, IDE assistants), 030 (may adopt the same token service for the mobile app) |

## 1. Problem and outcome

**Problem:** Customers want their other systems and their own AI tools to reach TeamBeats. A finance system wants to know when a request is completed. A company wants its employees' AI assistant to find a policy, read a page or raise a service request in TeamBeats. Today the only way in is the web app's own backend-for-frontend API, which is not a contract, uses browser cookies and changes with every release. Spec 017 covers the reverse direction (TeamBeats calling out), and spec 027 covers TeamBeats agents using tools, including external MCP servers in R3. Nothing defines how an outside caller proves who it is, which tenant it belongs to, what it may touch, how much it may call, and how an external model is kept inside the tenant's AI policy.

**Outcome:** Each tenant gets its own authorization server, a stable public REST API and a remote MCP server. An administrator registers an app, picks its scopes and decides whether it acts as itself or for an employee. Every call is tenant-bound from the token, checked by the central authorization service at the moment it runs, gated by entitlements, rate limited, and audited. A scope never grants anything by itself: an app acting for an employee can do at most what the employee can do, and an app acting as itself can do at most what its own roles allow. External AI clients reach TeamBeats through MCP tools that run through the same tool gateway as TeamBeats agents, so classification caps, write confirmation, limits and the kill switch apply to them too. Employees see and revoke the apps they connected.

## 2. Scope

**In scope:**
- A tenant authorization server (OAuth 2.1 with authorization server metadata, RFC 8414), where employees sign in through the tenant's own identity provider (spec 003) and never type a TeamBeats password.
- App registrations of four kinds: application (acts as itself, client credentials), delegated web app (confidential, authorization code with PKCE), delegated public app (native or single-page, PKCE, no secret), and MCP client.
- App credentials: client secrets shown once and stored as hashes, or a registered public key for signed client assertions (private key JWT, preferred), with expiry and overlapping rotation.
- A scope catalogue where each scope lists the API operations and MCP tools it covers, the authorization decisions it needs and the capability keys it requires.
- Application principals in spec 004: an application app gets a technical principal that can hold only roles marked assignable to apps, at a tenant, org unit, subtree or resource scope.
- Public REST API v1 under `/api/public/v1`, with its own OpenAPI document, separate from the internal web API.
- API conventions: versioning and deprecation, cursor pagination, idempotency keys, problem details errors, conditional requests, localization by `Accept-Language`, request IDs and correlation.
- Rate limits per app, per app and user, and per tenant, plus monthly call quotas from spec 005.
- Event subscriptions: thin, signed event notifications to an app's webhook URL, delivered by the outbound webhook runtime of spec 017, with the data fetched afterwards through the API under the app's own authorization.
- A remote MCP server per tenant over Streamable HTTP, with protected resource metadata (RFC 9728) and resource indicators (RFC 8707), offering read tools, resources and write tools that need confirmation in TeamBeats.
- External AI client policy as part of the tenant agent policy (spec 027): allowed MCP clients, tools, classification cap and whether write tools are allowed.
- Admin area "API and MCP access" in the admin shell, a connected apps page for each employee, consent screens, usage and call logs without payloads.
- Emergency controls: disable an app, revoke all its tokens, turn off the public API or external AI access for the whole tenant.

**Out of scope:**
- TeamBeats calling external systems, outbound webhooks for connections, inbound webhooks for connections and processes (spec 017; process messages in spec 024).
- TeamBeats agents using external MCP servers (spec 027 FR-006, R3).
- Admin APIs for users, roles, permissions, security policy, identity providers, entitlements and app management. They stay in the admin area in R2; SCIM provisioning is spec 003 FR-008 (R3).
- Content from connected sources such as SharePoint libraries and Outlook mail (specs 011 and 018). Apps read those from the source system directly.
- Static bearer API keys sent on every call (see section 17).
- A public marketplace of third-party apps (R3).

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| API administrator | Register, edit, disable and delete apps; set scopes, redirect URIs, IP ranges and classification caps; issue and revoke credentials (step-up); give admin consent; manage event subscriptions; see usage and call logs; revoke tokens | `api.manage` at tenant scope |
| API viewer | See apps, scopes, consents, usage and call logs | `api.view` |
| Role administrator | Assign app-assignable roles to an application principal within their own scope | `authz.roles_assign` (spec 004 FR-007) |
| AI Policy Administrator | Set the external AI client policy: allowed MCP clients, tools, classification cap, write tools | `ai.policy_manage` (spec 027) |
| Security officer | See apps, consents, call logs, alerts; disable an app; revoke tokens; use the tenant switches | `security.respond`, `audit.read` |
| Employee | Authorize an app to act for them within user-consentable scopes; see and revoke their connected apps; confirm or reject actions requested by an AI client | Own permissions |
| Application principal | Call the API as itself | Its granted scopes intersected with its own role assignments |
| Delegated app or MCP client | Call the API or MCP server for one employee | Its granted scopes intersected with the employee's current permissions |
| Platform operator | Maintain the list of verified MCP clients, disable an app type or client ID for all tenants, publish API versions | Platform |

The built-in Integration Administrator role holds `api.manage` and `api.view` at tenant scope; the Security Officer role holds `api.view`. Both permissions follow spec 004 FR-013.

## 4. User scenarios

### User Story 1: Register an app that acts as itself (Priority: P1, R2)

Ahmed, integration administrator at Acme, connects the finance system so it can read completed service requests and receive an event when one completes.

**Why this priority:** system-to-system integration is the most common enterprise request.

**Independent test:** register an application app, grant two scopes and a reader role at one org unit, obtain a token with client credentials, call an endpoint inside and outside the org unit, then disable the app and confirm calls fail.

**Acceptance scenarios:**
1. **Given** the "API and MCP access" area, **When** Ahmed creates an application app, **Then** he names it, picks scopes from the catalogue (each showing in plain language what it allows), and sees only scopes his package includes.
2. **Given** a new app, **When** he adds a credential, **Then** step-up is required (spec 026), he chooses a client secret or a public key, a secret is shown exactly once, and its expiry (default 6 months, at most 12) is set.
3. **Given** the app has the `requests:read` scope but no role, **When** it calls `GET /api/public/v1/requests`, **Then** it gets an empty list or not-found answers, because scopes never grant access on their own.
4. **Given** a role administrator assigns the app-assignable "Request reader" role at Acme Gulf, **When** the app reads requests, **Then** it sees Acme Gulf requests only; Acme Egypt requests return not-found (spec 004 FR-009).
5. **Given** a role that is not marked assignable to apps (for example Tenant Administrator), **When** anyone tries to assign it to an application principal, **Then** the save is refused.
6. **Given** a secret 14 days from expiry, **Then** API administrators are alerted, and a second secret can be added so both work until the old one is revoked.

### User Story 2: An employee connects an app that acts for them (Priority: P1, R2)

Mona uses a team planning tool that shows her TeamBeats tasks and lets her submit a service request.

**Acceptance scenarios:**
1. **Given** a delegated app, **When** Mona starts the connection in the app, **Then** she is sent to her tenant's TeamBeats sign-in page, signs in through Acme's identity provider (spec 003) with its MFA, and sees a consent screen in her language with the app name, whether its publisher is verified, and each scope in plain words.
2. **Given** a scope the tenant marks as admin consent only (by default every write scope), **When** no admin consent exists, **Then** Mona cannot grant it and the screen tells her to ask her administrator.
3. **Given** consent, **When** the app calls the API, **Then** each call is checked as Mona with her current permissions; a document she cannot open is not-found to the app too.
4. **Given** Mona's role is revoked, **When** the app calls again, **Then** the change applies within 60 seconds (spec 004 FR-008); if her membership is disabled, the app's tokens stop working at once (spec 003 FR-007).
5. **Given** the connected apps page in her profile, **When** Mona revokes the app, **Then** its refresh token and access tokens stop working within 60 seconds.
6. **Given** a guest user (spec 026), **When** they try to authorize any app, **Then** it is refused by default.

### User Story 3: Use AI assistants with TeamBeats through MCP (Priority: P1, R2)

Acme lets employees add TeamBeats to an approved AI client so they can ask about policies and documents, and file service requests, from that client.

**Acceptance scenarios:**
1. **Given** the AI Policy Administrator allows an MCP client from the verified list, **When** an employee adds the tenant's MCP server URL to that client, **Then** the client discovers the authorization server from the protected resource metadata, the employee signs in and consents as in User Story 2, and the client lists only the tools and resources the tenant allows, the package includes and the employee's scopes cover.
2. **Given** an MCP client that is not allowed by the tenant, **When** it tries to authorize, **Then** it is refused with a message naming the tenant policy, and the attempt is counted.
3. **Given** the `search` tool, **When** the client calls it, **Then** results are filtered by the employee's permissions (spec 004 FR-012) and by the external AI classification cap (default Internal); Confidential items need the cap raised by the AI Policy Administrator for that client, and Restricted items are never returned to external AI clients.
4. **Given** the `ask_teambeats` tool, **When** the client calls it, **Then** the answer comes from the assistant of spec 014 through the AI Gateway (spec 013) with citations, counts against the tenant AI budget, and follows the same classification cap.
5. **Given** the `submit_service_request` write tool, **When** the client calls it with parameters, **Then** nothing is submitted yet: TeamBeats creates a pending action, notifies the employee (spec 015), and the employee confirms or rejects it inside TeamBeats on a card that shows every parameter and marks it as requested by an external AI client. Only the confirmed parameters are submitted (spec 027 FR-010).
6. **Given** a pending action not confirmed within 15 minutes, **Then** it expires and the tool result tells the client it expired.
7. **Given** any MCP tool call, **When** it ends, **Then** an execution record exists in spec 027 with the client, the employee, the tool, masked parameters, the policy decisions and the outcome.
8. **Given** the AI Policy Administrator turns off external AI access, or the tenant agent kill switch is used (spec 027 US7), **Then** every MCP call is refused within 10 seconds while the REST API for non-AI apps keeps working.

### User Story 4: Receive events instead of polling (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** an application app with `events:subscribe` and `requests:read`, **When** Ahmed subscribes it to `RequestCompleted` with an allowlisted HTTPS URL, **Then** each matching event is sent as a signed notification with the event type, event ID, resource type, resource ID, resource URL and time, and no other content.
2. **Given** an event about a request the app cannot read under its roles, **When** it happens, **Then** no notification is sent, so a notification never reveals that a hidden resource exists.
3. **Given** a notification, **When** the app fetches the resource URL, **Then** the normal API authorization applies at that moment.
4. **Given** the receiver is down, **Then** delivery retries follow spec 017 (signed with HMAC SHA-256 over timestamp and body, retried, delivery log), and after 3 days of failures the subscription is paused and the API administrators are alerted.
5. **Given** an event type that is not in the public event catalogue (section 12), **Then** it cannot be subscribed to.

### User Story 5: Calls are limited, measured and explained (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** an app over its per-minute limit, **When** it calls again, **Then** it gets HTTP 429 with `Retry-After` and rate limit headers, and the call is counted, not processed.
2. **Given** the tenant reaches its monthly call quota (`api.monthly_calls`), **Then** further calls get 429 with a quota reason until the period resets or an add-on is applied, the web app keeps working, and API administrators are told at 80% and 100%.
3. **Given** the usage page, **When** an API administrator opens it, **Then** they see per app: calls, errors by class, authorization denials, rate limited calls, top operations and last use, over 90 days, without request or response bodies.
4. **Given** a tenant lowers an app's limit below the platform default, **Then** the lower value applies; a tenant can never raise a limit above the platform cap.

### User Story 6: Stop an app now (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** a leaked secret, **When** the security officer disables the app or revokes its tokens, **Then** new tokens are refused at once and existing access tokens are refused within 60 seconds.
2. **Given** a refresh token is used twice, **Then** the whole token family is revoked, the employee and API administrators are told, and a security alert is raised (spec 026 US5).
3. **Given** the tenant switch "public API off", **Then** every public API and MCP call is refused within 60 seconds, the web and mobile apps keep working, and the switch is audited with its reason.
4. **Given** the platform operator blocks an MCP client ID for a vulnerability, **Then** it stops working in every tenant and tenant administrators are notified.

### User Story 7: Developers find a stable, documented contract (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** the tenant's developer page, **When** a developer opens it, **Then** they see the public OpenAPI document, the scope catalogue, the event catalogue, the MCP tool and resource list, and the authorization server metadata URL, showing only what the tenant's package includes.
2. **Given** an operation marked deprecated, **When** an app calls it, **Then** the response carries `Deprecation` and `Sunset` headers, and the developer page lists the replacement and the date, at least 12 months after the notice.
3. **Given** a retried create call with the same `Idempotency-Key`, **Then** the action happens once and the same result is returned for 24 hours.

### Edge cases and exception flows
- A token issued by tenant A's authorization server used on tenant B's host: refused, counted, and written to the platform audit stream.
- A token whose audience is the REST API used on the MCP server, or the reverse: refused (resource indicators bind each token to one resource).
- The employee's identity provider is down: no new delegated tokens; existing access tokens work until they expire (10 minutes), refresh fails and the app sees a clear error.
- An MCP client changes its metadata or redirect URIs: authorization refuses URIs not registered or not in the verified metadata.
- An app's IP range rule: calls from outside the app's ranges are refused, and the tenant IP rules of spec 026 apply as well.
- An application principal whose role is removed: next call within 60 seconds sees less.
- A write tool call whose target came from content the AI client read (for example a recipient taken from a document): the confirmation card says it did not come from the employee (spec 027 US5 scenario 4).
- An app tries to page past 10,000 results with offsets: only cursors are offered; very large exports use the owning module's export feature, not the API.
- A tenant downgrades and loses `api.mcp_write_tools`: write tools disappear from the tool list within 60 seconds and pending actions expire.

## 5. Functional requirements

- **API-001 / FR-001**: Each tenant MUST have its own OAuth 2.1 authorization server with issuer `https://<tenant host>/oauth`, metadata at the RFC 8414 location, and signing keys that are separate per tenant. In dedicated and on-premises installs the issuer is the install's host.
- **API-002 / FR-002**: Every public API and MCP request MUST resolve its tenant from the host and validate that the token's issuer and tenant claim match it. Tenant identity MUST NOT come from any other client input. *(constitution I)*
- **API-003 / FR-003**: Employees MUST sign in to the authorization server only through the tenant's configured identity provider (spec 003). The authorization server MUST NOT hold passwords. *(constitution V)*
- **API-004 / FR-004**: App registrations MUST be tenant-owned records of kind application, delegated web, delegated public or MCP client, with exact-match redirect URIs (HTTPS, or loopback for native apps), PKCE required for every authorization code flow, and the implicit and password grants not supported.
- **API-005 / FR-005**: App credentials MUST be client secrets stored only as salted hashes and shown once, or registered public keys for private key JWT client assertions. Adding or replacing a credential MUST require step-up. Expiry MUST default to 6 months with a maximum of 12, with up to two active credentials for rotation.
- **API-006 / FR-006**: Access tokens MUST be short-lived (default 10 minutes, at most 60), bound to one resource (REST API or MCP server) through resource indicators, and carry the tenant, app, principal, scopes and a token family ID. Refresh tokens MUST rotate on every use, be single use, expire after 30 days by default (tenant may lower), and trigger family revocation on reuse.
- **API-007 / FR-007**: Revoking an app, a consent, a credential or a token family MUST refuse new tokens at once and existing access tokens within 60 seconds. Disabling a user or membership MUST refuse their delegated tokens at once. *(spec 003 FR-007, spec 004 FR-008)*
- **API-008 / FR-008**: The scope catalogue (section 6) MUST be the only list of scopes. Each scope MUST list its operations and MCP tools, the authorization decisions they make and the capability keys they need. A new scope or a scope covering more operations is a change to this spec.
- **API-009 / FR-009**: Scopes MUST only narrow. Effective access for a delegated call MUST be the intersection of the granted scopes and the user's current permissions; for an application call, the intersection of the granted scopes and the application principal's role assignments. A scope MUST NOT grant access by itself. *(constitution III)*
- **API-010 / FR-010**: Every public operation MUST call the central authorization service at execution time and the entitlement service with capability keys. Unreadable resources MUST return not-found. *(spec 004 FR-009, constitution XXI)*
- **API-011 / FR-011**: Application principals MUST be spec 004 principals in one tenant that can hold only roles marked assignable to apps. Roles holding any `authz.*`, `identity.*`, `security.*`, `api.*`, `audit.export` or `ai.policy_manage` permission MUST NOT be assignable to apps.
- **API-012 / FR-012**: The public API MUST NOT expose operations that change roles, permissions, policies, identity providers, security settings, entitlements or app registrations. *(spec 027 FR-012 applies the same rule to tools)*
- **API-013 / FR-013**: User consent MUST be possible only for scopes the tenant marks user-consentable (default: read scopes). Other scopes MUST need admin consent by an API administrator. Guests MUST NOT authorize apps unless the tenant allows it per app.
- **API-014 / FR-014**: The public API MUST live under `/api/public/v<major>` with its own OpenAPI document, versioned per constitution XVI and XXIX: additive changes only within a major version, a deprecation period of at least 12 months with `Deprecation` and `Sunset` headers, and an OpenAPI diff check in CI. The internal web API is not a public contract.
- **API-015 / FR-015**: Public operations MUST use cursor pagination (page size default 50, at most 200), problem details errors with stable error codes, `ETag` with conditional requests on readable resources, `Idempotency-Key` on create and action operations with a 24 hour replay window, and a request ID echoed in responses and linked to the correlation ID. *(constitution XVI, XIX)*
- **API-016 / FR-016**: Responses MUST localize display text by `Accept-Language` within the tenant's supported locales and MUST NOT localize identifiers, codes or enum values.
- **API-017 / FR-017**: Rate limits MUST apply per app, per app and user, and per tenant, with platform defaults and caps (section 6) that a tenant may only lower. Over-limit calls MUST get HTTP 429 with `Retry-After` and rate limit headers. Monthly call quotas MUST be checked through `api.monthly_calls`.
- **API-018 / FR-018**: Event subscriptions MUST only cover the public event catalogue, MUST carry identifiers and links only, MUST be filtered so a principal that cannot read the resource gets no notification, and MUST be delivered through the spec 017 outbound webhook runtime with its signing, retry, host allowlist and delivery log. *(017 CON-012, CON-013)*
- **API-019 / FR-019**: The MCP server MUST be served per tenant at `https://<tenant host>/mcp` over Streamable HTTP, publish protected resource metadata (RFC 9728) pointing to the tenant authorization server, and accept only delegated tokens issued for it. Application tokens MUST NOT be accepted by the MCP server in R2. *(constitution X)*
- **API-020 / FR-020**: MCP tools MUST be entries of the spec 027 tool registry marked as exposable to external AI clients, and every MCP tool call MUST go through the spec 027 tool gateway with the caller type "external AI client", in the same check order (027 US3), plus the external AI client policy.
- **API-021 / FR-021**: The external AI client policy MUST let the AI Policy Administrator choose allowed MCP clients, allowed tools, the maximum classification returned (default Internal, Confidential by explicit choice per client, never Restricted), and whether write tools are allowed (default off). Org unit policies MAY only restrict. *(constitution IX, spec 027 FR-018)*
- **API-022 / FR-022**: MCP write tools MUST NOT act until the employee confirms the exact parameters inside TeamBeats. Confirmations MUST be bound to a parameter hash, expire after 15 minutes, and show that the request came from an external AI client. Approval rules of spec 027 US4 MUST apply on top, and tools whose rule is "block" MUST NOT be exposable. *(constitution X, spec 027 FR-010)*
- **API-023 / FR-023**: MCP clients MUST be identified by a client ID metadata document from the platform's verified list or by a registration the API administrator created. Open dynamic client registration MUST be off; a tenant MAY turn on dynamic registration with each new client held for API administrator approval.
- **API-024 / FR-024**: Tool results and resources returned to external AI clients MUST carry the source reference and classification of each item, and content MUST be returned as data fields, never as instructions to the client.
- **API-025 / FR-025**: The MCP server MUST NOT call models except through the `ask_teambeats` tool, which uses spec 014 and the AI Gateway (spec 013) and counts against the tenant AI budget. *(constitution VIII)*
- **API-026 / FR-026**: Every public API write call, every MCP tool call, every credential, consent, subscription and app change, and every refused token MUST be audited or recorded as in section 13, without tokens, secrets or bodies. *(constitution VI)*
- **API-027 / FR-027**: Tenant IP rules and per-app IP ranges MUST apply to the authorization server, the public API and the MCP server. *(spec 026 FR-001)*
- **API-028 / FR-028**: Emergency controls MUST include disable app, revoke app tokens, revoke an employee's app consents, tenant switches for the public API and for external AI access, and a platform block for an MCP client ID or app, with the timings of User Story 6.
- **API-029 / FR-029**: Employees MUST have a connected apps page listing each app they authorized with scopes, consent date and last use, with revoke; administrators MUST see all consents in the tenant.
- **API-030 / FR-030**: The public API, MCP server and authorization server MUST keep working when any AI provider is down, except the `ask_teambeats` tool, which returns a clear unavailable result. *(constitution XII)*

## 6. Business and validation rules

**Scope catalogue (R2).** Scopes use `<resource>:<verb>` so they never collide with permission keys (`<area>.<action>`) or capability keys (dotted lower case).

| Scope | Operations and MCP tools | Authorization decision | Capability keys | Consent | Allowed for |
|---|---|---|---|---|---|
| `profile:read` | Signed-in user's own profile and memberships (`whoami`) | Own record | `api.public` | User | Delegated |
| `content:read` | News, announcements, pages (009); tools `get_page`, `list_news`; resources `teambeats://pages/{id}` | CanView | `api.public` | User | Both |
| `content:draft` | Create a news or page draft, never publish (009); tool `draft_news` | CanEdit, draft only | `api.write_scopes` | Admin | Delegated |
| `documents:read` | Libraries, documents, versions, download links from the built-in store (011); tool `get_document`; resources `teambeats://documents/{id}` | CanView | `api.public` | User | Both |
| `documents:write` | Upload a document or a new version to a library (011) | CanEdit | `api.write_scopes` | Admin | Both |
| `people:read` | Directory profiles within field policy, org chart, reporting line (010); tool `find_person`; resources `teambeats://people/{id}` | CanView with field policy | `api.public` | User | Both |
| `org:read` | Organization units and hierarchy (002) | CanView | `api.public` | User | Both |
| `policies:read` | Published policies and own attestation status (025); tool `get_policy` | CanView | `api.public` | User | Both |
| `search:query` | Search across permitted content (012); tool `search` | Batch filter | `api.public` | User | Both |
| `assistant:ask` | Cited answers (014); tool `ask_teambeats` | Batch filter on sources | `api.public`, `ai.assistant` | User | Delegated |
| `services:read` | Service catalogue (016); tool `list_services` | CanView | `api.public` | User | Both |
| `requests:read` | Requests the principal can see (016); tool `list_my_requests` | CanView | `api.public` | User | Both |
| `requests:submit` | Submit a request for oneself (016), and for others where 016 allows; tool `submit_service_request` | CanExecute | `api.write_scopes` | Admin | Both |
| `tasks:read` | The principal's task inbox items (016); tool `list_my_tasks` | Own assignments | `api.public` | User | Delegated |
| `events:subscribe` | Create and manage the app's own event subscriptions | Own app | `api.event_subscriptions` | Admin | Application |

Approving or rejecting tasks, publishing, attesting a policy and deleting anything are not in the R2 catalogue: they stay human actions inside TeamBeats. Modules of R2 specs that are not listed (023, 028, 029, 031, 022) join the catalogue by a change to this table, additively.

**MCP surface (R2).** Read tools: `whoami`, `search`, `ask_teambeats`, `get_page`, `list_news`, `get_document`, `find_person`, `get_policy`, `list_services`, `list_my_requests`, `list_my_tasks`. Write tools, each with confirmation in TeamBeats: `submit_service_request`, `draft_news`. Resources: pages, documents, policies and people by URI, readable and listed through `resources/list` within the same filters. No MCP prompts in R2. Tool names and schemas are versioned like the REST API: within a major version a tool only gains optional fields; a breaking change is a new tool name.

**Rate limits (platform defaults and caps).**

| Limit | Default | Platform cap |
|---|---|---|
| Per application app | 600 calls per minute | 3,000 |
| Per delegated app and user | 120 calls per minute | 600 |
| Per tenant, all apps | 6,000 calls per minute | Set per installation |
| MCP tool calls per user and client | 60 per minute | 300 |
| `ask_teambeats` per user and client | 20 per hour | Spec 013 budgets apply |
| Token requests per app | 60 per minute | 300 |

**Other rules.**
- Request body at most 1 MB, except document uploads, which follow spec 011 size limits.
- Event notifications are thin (identifiers and links only); payload size at most 4 KB.
- Call logs keep operation, status, latency, app, principal, error class and correlation ID for 90 days; no bodies, no query strings with personal data.
- An app that is unused for 180 days is flagged; a credential unused for 90 days is flagged.
- Consent screens and app names are shown as text, never as markup; app logos are sanitized images (spec 001 rules).

## 7. Tenant and organization scope
- Tenant-owned data: app registrations, credentials (hashes or public keys), consents, token families, revocations, application principals, event subscriptions, rate limit settings, usage counters, call logs, external AI client policy (stored with the spec 027 tenant agent policy).
- Platform data: the verified MCP client list, platform caps, the scope and event catalogues, the public OpenAPI document. They contain no tenant data.
- Organization scoping: application principals hold roles at org unit, subtree or resource scope (spec 004 FR-003); delegated calls carry the user's scope. App administration is tenant-wide in R2.
- Cross-tenant denial cases: tenant A's token on tenant B's host; tenant A's app ID used in tenant B's authorization flow; an event subscription delivering a tenant B event to a tenant A app; a call log or consent of tenant B shown to tenant A; an MCP session reused across tenants. All MUST be refused and covered by tests.
- Sibling, parent and child cases: an application principal with a role at Acme Gulf cannot read Acme Egypt resources, nor Acme Group resources above it, nor child units unless the role was given at subtree scope.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| App registration, scopes, redirect URIs | Product (`PublicApi`) | Internal | Until deleted, then 90 days | No |
| App credential (hash or public key) | Product (`PublicApi`) | Restricted | Until expired or revoked, then 90 days of metadata | No |
| Signing keys per tenant | Secret manager | Restricted | Rotated every 90 days, old keys kept for validation for 24 hours | No |
| Consent | Product (`PublicApi`) | Internal | Until revoked, then 1 year | Yes (user ID) |
| Token family and revocation list | Product (`PublicApi`) | Confidential | Until expiry plus 7 days | Yes (user ID) |
| Event subscription | Product (`PublicApi`); deliveries in `Integrations` delivery log | Internal | Until deleted | No |
| Call log | Product (`PublicApi`) | Confidential | 90 days | Yes (principal ID) |
| Usage counters | `Entitlements` (spec 005 usage) | Internal | 2 years | No |
| MCP execution records | `Agents` (spec 027) | Confidential | Spec 027 section 8 | Yes |

No access token or refresh token is stored in clear text. Refresh tokens are stored as hashes.

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `api.public` (authorization server, read scopes, REST API) | on | on | on |
| `api.write_scopes` (write scopes) | off | on | on |
| `api.event_subscriptions` | off | on | on |
| `api.mcp` (MCP server with read tools and resources) | on | on | on |
| `api.mcp_write_tools` (confirmed write tools) | off | on | on |
| `api.max_apps` (limit) | 2 | 20 | unlimited |
| `api.monthly_calls` (limit) | [TBD] | [TBD] | [TBD] |

`api.mcp` is on in every package because MCP access is an AI capability (constitution XIII); packages differ by write tools, app count and quota. `ai.agents.mcp` in spec 027 is a different direction: TeamBeats agents using external MCP servers (R3).

## 10. UX, localization and accessibility
- Admin area "API and MCP access" with tabs: Apps, Consents, Event subscriptions, Usage, Developer page, Settings (tenant switches, user consent rules, dynamic registration). An app page has tabs for scopes, credentials, redirect URIs, IP ranges, roles (link to the spec 004 assignment screen), subscriptions, usage and call log.
- External AI clients appear in the agent policy page of spec 027 under "External AI clients".
- Employee profile: "Connected apps" with revoke; the notification centre and task inbox show pending AI client actions with Confirm and Reject.
- Consent and confirmation screens in Arabic and English (French, Urdu and Hindi with spec 007 R2), RTL, keyboard operable, readable by screen readers, WCAG 2.2 AA. Scope descriptions are resource keys.
- The developer page renders the OpenAPI document and catalogues in English; descriptions of scopes and tools are localized.

## 11. AI behavior
- External AI clients are outside the tenant's AI Gateway policy: TeamBeats cannot see or control the model they use. The classification cap and tool policy of FR-021 are therefore the control, enforced on the server, never in the client.
- Tool results are data. Content may contain text that tries to steer the client's model; TeamBeats marks each item with its source and classification (FR-024) but cannot stop the external model from following it, which is why write tools need confirmation inside TeamBeats (FR-022).
- `ask_teambeats` follows spec 014 behavior: cited answers from permitted content, a no-evidence answer when nothing permitted is found, and the injection handling of spec 027 US5 inside TeamBeats.
- Evaluation: the spec 034 permission isolation, data classification and unsafe action suites gain cases for the external AI caller type (FR-020 to FR-022).

## 12. Integrations and events
- **Public event catalogue (R2):** `ContentPublished`, `DocumentVersionPublished`, `PolicyPublished`, `RequestSubmitted`, `RequestCompleted`, `ProfileUpdated` (identifier only), `OrganizationUnitMoved`. Each public event has its own versioned schema in `PublicApi.Contracts`, derived from the internal event; internal events are never sent as they are.
- Publishes `AppRegistered`, `AppDisabled`, `AppCredentialRevoked`, `AppConsentGranted`, `AppConsentRevoked`, `TokenFamilyRevoked`, `ExternalAiAccessChanged`.
- Consumes `UserDeprovisioned`, `SessionRevoked` (003), `RoleAssignmentChanged`, `ResourcePermissionChanged` (004), `EntitlementsChanged` (005), `AgentKillSwitchActivated`, `ToolDisabled`, `AgentActionAwaitingApproval` (027), and the internal events behind the public event catalogue.
- Uses the `Integrations` outbound webhook delivery contract (spec 017 US5) for event notifications.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| api.app_registered / changed / disabled / deleted | audit | app, kind, scopes, actor |
| api.credential_added / revoked / expired | audit | app, credential ID and type (no secret), actor |
| api.consent_granted / revoked | audit | app, user or admin consent, scopes, actor |
| api.role_assigned_to_app | audit (spec 004) | app principal, role, scope, actor |
| api.token_family_revoked | audit | app, principal, reason (user, admin, reuse detected) |
| api.token_refused | audit for cross-tenant and revoked, telemetry otherwise | app, reason, host tenant |
| api.write_call | audit | app, principal, operation, resource, outcome, idempotency key |
| api.subscription_created / paused / deleted | audit | app, event type, URL host, actor |
| api.tenant_switch_changed | audit | switch, value, reason, actor |
| agent.tool_called / tool_denied (caller external AI client) | audit (spec 027) | client, user, tool, parameter hash, outcome |
| api.call | telemetry | app kind, operation, status, latency, rate limited (no content) |

Audit and call log events are part of the SIEM stream of spec 026 FR-014.

## 14. Security and abuse cases
- Token theft: short access tokens, rotating single-use refresh tokens with reuse detection, per-app IP ranges, revocation within 60 seconds; sender-constrained tokens in R3.
- Confused deputy across tenants: per-tenant issuer and keys, host to issuer check, resource-bound audiences (FR-001, FR-002, FR-006).
- Privilege escalation through scopes: scopes only narrow (FR-009), admin capabilities are not in the API (FR-012), app-assignable roles exclude admin permissions (FR-011).
- Malicious or fake MCP client: verified client list, exact redirect URIs, no open dynamic registration, tenant allowlist, platform block (FR-023, FR-028).
- Data exfiltration through an external model: classification cap with Restricted never returned, permission filtering, rate limits, call logs and security alerts for volume (FR-021, FR-017, spec 026 US5).
- Prompt injection driving a write: confirmation inside TeamBeats bound to the parameter hash (FR-022).
- Hidden resource discovery: not-found semantics and filtered event notifications (FR-010, FR-018).
- Consent phishing: write scopes need admin consent by default, publisher verification shown, guests cannot authorize apps (FR-013).
- Server-side request forgery through webhook URLs: spec 017 host allowlist and resolved address checks (CON-012).
- Abuse of compute through the ask tool: per-user limits and spec 013 budgets.

## 15. Non-functional requirements
- Token validation and authorization overhead under 10 ms p95 per call, excluding the operation.
- MCP tool gateway overhead under 100 ms p95, excluding the operation.
- Token endpoint p95 under 300 ms excluding the identity provider round trip.
- 99.9% monthly availability for the authorization server and public API in shared SaaS, the same as the web app.
- Revocation propagation within 60 seconds across all API instances.

## 16. Success criteria
- **SC-001**: An API administrator registers an application app, assigns a role and receives its first event notification in under 30 minutes without developer help.
- **SC-002**: An employee adds the tenant's MCP server to an allowed AI client, signs in and gets a cited answer in under 5 minutes.
- **SC-003**: The cross-tenant, org boundary, scope narrowing, revocation and classification cap suites pass on every build.
- **SC-004**: No MCP write tool acts without a confirmation recorded in TeamBeats, shown by the unsafe action suite of spec 034.

## 17. Open questions
- [NEEDS CLARIFICATION: should TeamBeats offer static API keys sent on every call, for simple scripts?] Default: no. Client credentials with a secret or a private key give the same ease with short-lived tokens and are revocable within 60 seconds.
- [NEEDS CLARIFICATION: may an AI client confirm a write action inside its own interface through MCP elicitation?] Default: no in R2; confirmation happens inside TeamBeats. Revisit in R3 for verified clients.
- [NEEDS CLARIFICATION: which MCP clients go on the first verified list?] Default: Claude, ChatGPT, Microsoft Copilot Studio and Visual Studio Code, each added after a security review of its client metadata.
- [NEEDS CLARIFICATION: monthly call quotas per package] Default: set with the commercial model, like spec 005 C1 to C3; values are data and do not block the build.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| Foundation principle 8 (OAuth 2.0, REST, OpenAPI) | FR-001, FR-004, FR-014 |
| C-07 API contracts, constitution XVI | FR-014, FR-015 |
| Constitution I, II | FR-002, section 7 |
| Constitution III, XXI | FR-009 to FR-012 |
| Constitution VII to XI | FR-020 to FR-025 |
| UC-07 | US3, FR-022 |
| UC-10 | US1, US4 |
