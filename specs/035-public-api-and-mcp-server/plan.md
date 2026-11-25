# Implementation Plan: Public API, MCP Server and App Access

**Spec:** `specs/035-public-api-and-mcp-server/spec.md` (v1.0, Approved) | **Date:** 2026-11-08
**Shared base:** ADR-0018 (authorization server and MCP hosting), ADR-0005 (tenant-scoped identity), ADR-0009 (Npgsql), ADR-0013 (ports and adapters), ADR-0006 (outbox), spec 017 plan (outbound webhook runtime), spec 027 plan (tool registry and gateway)

## Summary
A new `PublicApi` module owns app registrations, credentials, consents, token families, application principal links, event subscriptions, rate limit settings and call logs. It hosts the per-tenant authorization server with OpenIddict on custom Npgsql stores, a token validation and rate limiting pipeline for `/api/public/v1` and `/mcp`, and the MCP server with the official MCP C# SDK. Owning modules keep their own public endpoints in their own projects, registered through a `PublicEndpoint` convention that declares the scope, decision and capability key, so business rules stay in the module. MCP tools are adapters over the spec 027 tool registry and call the tool gateway; nothing in `PublicApi` decides access on its own.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I tenant isolation | Yes | Tenant from host, issuer and tenant claim must match; all `PublicApi` tables tenant-owned with forced RLS; per-tenant signing keys; tenant in cache keys for revocation and rate limit partitions |
| II organization scope | Yes | Application principals hold scoped roles in `Authorization`; delegated calls use the user's scope; boundary tests per endpoint |
| III central authorization | Yes | Every public endpoint calls `IAuthorizationService`; the scope check is an extra narrowing filter, never a grant |
| V no product passwords | Yes | Authorization server signs users in only through the spec 003 identity provider flow |
| VI audit | Yes | Section 13 events through `IAuditWriter` |
| VIII, IX AI | Yes | Only `ask_teambeats` reaches models, through `Agents` and `AiGateway`; classification cap enforced in the tool gateway |
| X human control | Yes | MCP write tools create pending confirmations in TeamBeats; no application tokens on MCP |
| XIV module boundaries | Yes | `PublicApi` reads other modules only through their `Contracts`; endpoints stay in their modules |
| XVI API contracts | Yes | Separate `openapi.public.v1.json` with a CI diff check; idempotency keys |
| XXI entitlements | Yes | Capability keys of section 9 through `IEntitlementService` |
| XXII dependencies | Yes | OpenIddict (Apache 2.0) and ModelContextProtocol.AspNetCore (Apache 2.0), reasons in ADR-0018 |

## Module and projects
- `src/Modules/PublicApi` (Domain, Application, Infrastructure in the ADR-0013 layout) and `src/Modules/PublicApi.Contracts` (public event schemas, `IPublicEndpointRegistry`, `IAppPrincipalLookup`).
- Module map gains `PublicApi` (owner of the data in spec section 8).
- `Authorization` gains the principal type `app` and the role flag `assignable_to_apps`; the architecture test from spec 004 rejects app-assignable roles that hold excluded permissions (FR-011).
- `Agents` gains the caller type `external_ai_client` in the tool gateway, the "exposable to external AI clients" flag on registry entries, the external AI client section of the tenant agent policy and pending confirmations from external clients.
- `Integrations` exposes `IWebhookDelivery` from its spec 017 runtime for thin event notifications.

## Data model (schema `public_api`, forced RLS)
- `app` (kind, name, publisher, verified, status, redirect URIs, IP ranges, classification cap, rate limit overrides, metadata document URL for MCP clients)
- `app_credential` (type secret or public key, hash or JWK, created, expires, last used, revoked_at)
- `consent` (app, user or admin, scopes, granted_at, revoked_at)
- `token_family` (app, principal, resource, created, last refreshed, revoked_at, reason) and `refresh_token` (hash, family, used_at)
- `app_principal` (app to `Authorization` principal ID)
- `event_subscription` (app, event type, version, URL, status, paused_at)
- `call_log` (partitioned by day, 90 days) and `tenant_api_settings` (switches, user consent rules, dynamic registration)
- Platform tables without tenant: `verified_mcp_client`, `platform_api_caps` (listed in `docs/architecture/rls-exceptions.md`).

## Authorization server
- OpenIddict server with per-tenant issuer chosen from the host, degraded to the install host in dedicated installs. Custom stores on `IDbSession` (ADR-0009), no EF Core.
- Authorization endpoint redirects to the spec 003 sign-in when there is no session for the tenant, then shows the consent screen (React page in `src/Web`, data from `PublicApi`).
- Grants: authorization code with PKCE, refresh token, client credentials. Client authentication: `client_secret_post`, `client_secret_basic`, `private_key_jwt`. Resource indicators bind tokens to `api` or `mcp`.
- Signing keys per tenant from the secret manager, rotated every 90 days.
- Client ID metadata documents for MCP clients are fetched through the spec 017 `SafeHttpClient` (SSRF guard) and cached; only clients on the verified list or the tenant's own registrations are accepted.

## Request pipeline for `/api/public` and `/mcp`
1. Network rules (spec 026) and per-app IP ranges.
2. Token validation: signature with the host tenant's keys, issuer, audience, expiry, tenant claim against host tenant.
3. Revocation check against a cached list keyed by tenant and token family, refreshed from events and at most 60 seconds old.
4. Tenant switches and entitlement check (`api.public` or `api.mcp`).
5. Rate limiting with the ASP.NET Core rate limiter, partitions per tenant, app and user; counters per instance scaled by the instance count, monthly quota through spec 005 usage counters.
6. Endpoint: scope check from the `PublicEndpoint` declaration, then the module's own authorization and handler.
7. Call log and telemetry.

## Public endpoints
- Modules declare `MapPublicGet/Post(...).RequireScope("content:read").Decides(CanView)` in their own module projects. An architecture test fails if a `/api/public` endpoint has no scope declaration or if a declared scope is not in the catalogue.
- A separate OpenAPI document is generated from public endpoints only (`docs/architecture/openapi.public.v1.json`), with an OpenAPI diff check in CI that fails on a breaking change within v1.
- Idempotency: a shared `IdempotencyStore` in `PublicApi` keyed by tenant, app, key and request hash, 24 hours.

## MCP server
- `ModelContextProtocol.AspNetCore` with Streamable HTTP, mapped at `/mcp`, stateless request handling with no cross-tenant session cache.
- `tools/list` is computed per call from the tool registry (exposable entries), the tenant's external AI client policy, entitlements and the token's scopes.
- `tools/call` builds a tool gateway request with caller type `external_ai_client` and the user principal; the gateway runs its checks, applies the classification cap, and for write tools returns a pending confirmation result with a TeamBeats link.
- Resources map URIs to module read contracts with the same filters.
- Protected resource metadata at `/.well-known/oauth-protected-resource/mcp`.

## Event subscriptions
- `PublicApi` consumes the internal events behind the public catalogue from the outbox, maps them to public event schemas, filters by batch authorization for each subscribed app principal, and hands thin payloads to `IWebhookDelivery`.
- Pausing after 3 days of failures uses the delivery log status from `Integrations`.

## Admin and employee pages
- Admin "API and MCP access" pages in `src/Web` (Administration area), employee "Connected apps" in the profile area, consent and confirmation pages. All strings as resource keys in Arabic and English.

## Tests
- Unit: scope narrowing property test (adding a scope never grants a decision the role or user lacks), excluded permission rule for app-assignable roles, rate limit partitions, refresh token reuse detection.
- Integration on PostgreSQL: forced RLS on all `public_api` tables; cross-tenant token, app ID, subscription and call log cases; org boundary per public endpoint; revocation within 60 seconds; entitlement allowed, denied, quota exhausted, add-on and expiry for each key in section 9; IP range refusal.
- MCP: tools list per policy and scopes; classification cap with Internal, Confidential and Restricted fixtures; write tool creates a pending confirmation and acts only after confirm; kill switch within 10 seconds; injected content in a document returned as data with source and classification.
- Contract: OpenAPI diff check; public event schema compatibility.
- Fakes: a fake MCP client and a fake OAuth client in `DevFakes` for e2e.
- Acceptance (e2e): register an application app, assign a role, call the API, receive an event; employee consent and revoke; MCP sign-in, search, ask and a confirmed service request, in English and Arabic with axe.

## Complexity tracking
| Deviation | Why | Simpler option rejected |
|---|---|---|
| Two new libraries | OAuth 2.1 server and MCP transport are security-critical protocols | Writing our own: higher risk and more code to audit (ADR-0018) |

## R3 plan

R3 adds sender-constrained tokens, MCP prompts, confirmation inside verified AI clients, admin APIs beside SCIM, a developer sandbox tenant and a public app gallery. The spec names them in the release line and the open questions; the defaults below are the smallest that keep the R2 security model (scopes only narrow, admin rights never reach an app by accident, every write confirmed by a person).

R3 slices, in order:
1. **Sender-constrained tokens (DPoP):** proof of possession for access and refresh tokens.
2. **MCP prompts:** a small, localized prompt catalogue built on the existing tools.
3. **Admin APIs beside SCIM:** read-only role administration (roles and who holds them) for a person acting through a delegated app, as FR-012 allows no public operation that changes roles.
4. **Developer sandbox tenant:** a short-lived tenant with demo data for building against the API.
5. **Public app gallery:** platform-published apps a tenant adds with admin consent.
6. **Confirmation inside verified AI clients (MCP elicitation):** opt-in per tenant, for verified clients that support it.

### Sender-constrained tokens
- **DPoP (RFC 9449) only.** Mutual TLS needs client certificates passed through the customer's proxy and is not offered; the notes record it.
- **Per app:** a setting "require DPoP", off by default so R2 clients keep working, set by API administrators on the app page. With it on, tokens issued earlier without a binding are refused too. A token request with a valid `DPoP` header always gets a bound token (`cnf.jkt`, `token_type` `DPoP`), whatever the setting; with the setting on, a request without one is refused (`invalid_dpop_proof`).
- **Proofs:** `typ` `dpop+jwt`, `ES256` (EC P-256), `RS256` or `PS256` (RSA of at least 2048 bits), the same keys as private key JWT, with a public `jwk` and no private parts; `htm` and `htu` (scheme, host and path, no query) match the request; `iat` at most 5 minutes old and at most 60 seconds ahead; `jti` used once per tenant and key within that window; on resource calls `ath` is the hash of the access token, and at the token endpoint there is none. No server nonce in this slice.
- **Resource calls** with a bound token need `Authorization: DPoP <token>` and a matching proof on `/api/public` and `/mcp`; a bound token sent as `Bearer` is refused. A refresh token issued with a binding only refreshes with a proof from the same key, and one issued without stays without. The authorization server metadata lists `dpop_signing_alg_values_supported`.
- **Errors** follow RFC 9449: `invalid_dpop_proof` at the token endpoint, `WWW-Authenticate: DPoP error="invalid_dpop_proof"` on resource calls. Refusals are audited as `api.token_refused` with reason `dpop`.

### MCP prompts
- **Catalogue in code**, five prompts built on R2 tools: `catch_up_on_news` (list_news), `find_a_policy` (search, get_policy; argument `topic`), `who_can_help` (find_person; `need`), `my_open_items` (list_my_requests, list_my_tasks), `request_a_service` (list_services, submit_service_request; `need`). Tenants do not edit prompts in R3.
- **Filtered like tools:** a prompt is listed and served only when the tool gateway offers the caller every tool it names, so scopes, the package, the external AI client policy (write tools, cap) and the kill switch apply unchanged.
- **Localized** from the API catalogue by `Accept-Language` within the tenant's locales, as other public answers.
- **Arguments:** short text (at most 300 characters), required where marked, no unknown names; put in the text in quotes with quotes and line breaks removed. Every text tells the client that what TeamBeats returns is information, not instructions. A prompt reads no TeamBeats data itself.

### Admin APIs beside SCIM
- **Read only.** FR-012 forbids public operations that change roles, permissions, policies, identity providers, security settings, entitlements or apps, so R3 adds reads only: `GET /api/public/v1/admin/roles` and `GET /api/public/v1/admin/role-assignments` (optional `userId`, cursor pages). Changing roles over the API would need FR-012 amended first; it is an open decision.
- **Scope `roles:read`:** delegated apps only, never an employee's own consent (admin consent), capability `api.admin` (Enterprise and Government). Application apps cannot hold it, so FR-011 stays as it is: apps never hold administration permissions.
- **Same use cases as the admin area** with the person's own rights: `authz.roles_view` coverage decides what comes back, so a unit administrator sees their units only. Apps' own role assignments and attribute conditions are left out of the answer.
- SCIM (spec 003 FR-008) stays the way to provision people; directory, security, identity provider, entitlement and app administration stay in the admin area.

### Developer sandbox
- **Made by platform operators only**, as a tenant with an end date: the create tenant request takes `sandboxDays` (1 to 30). There is no self-service sign-up in R3.
- **Lower API limits until the end date:** entitlement overrides that end on that date set `api.monthly_calls` to 10,000 and `api.max_apps` to 5, whatever the package gives. Other capabilities follow the package.
- **The developer page says when it ends**, and the platform tenant list shows the date.
- **Expiry:** an hourly worker suspends a sandbox when its date passes (audited as `tenant.status_changed` with reason `sandbox_expired`) and closes it 14 days later, which starts the usual retention and deletion. Tenants without a date are never touched.
- **Demo data:** a sandbox starts empty in production. On staging and the development host the existing demo data action fills it; a production demo pack is a gap.

### Tests (R3 slice 4)
- A sandbox gets the lower limits, shows its end date on the developer page and in the platform list, is suspended after the date and closed 14 days later; a live tenant is left alone; 0 or 31 days are refused; a tenant administrator cannot create one.

### Public app gallery
- **Platform list, like the verified MCP clients:** the platform operator publishes an app after reviewing its publisher's client ID metadata document, recording name, publisher, description, home page, redirect URIs and scopes. A client ID is either a verified AI client or a gallery app, never both. Withdrawing an entry disables every tenant's registration of it at once, tokens included. There is no publisher self-service in R3, and no platform screen yet (API only, as the verified list).
- **Delegated apps with PKCE only:** gallery apps keep no secret, so no tenant hands a credential to a publisher. Application apps stay registered by each tenant.
- **Added by an API administrator** on a new "App gallery" tab of the API page. Adding registers the app (the gallery owns its name, publisher, scopes and redirect URIs; the tenant keeps its switches and limits) and gives the admin consent for all its scopes, so employees connect it without asking. Adding needs the API manage right, a package that includes every scope (shown on the tab) and room under the app limit. Adding it again changes nothing. Removing consent, disabling and revoking work as for any app.
- **The consent screen** shows the publisher as verified, as for verified AI clients.

### Tests (R3 slice 5)
- An administrator adds a published app, gets the registration with admin consent for its scopes, and an employee connects it at once; employees cannot see or add; the tenant cannot rename it; another tenant sees it unadded and refuses the token; withdrawing blocks it and its tokens; only platform operators publish; a verified AI client ID, a plain name or an unknown status is refused; scopes outside the package are shown and refused.

### Tests (R3 slice 1)
- A client credentials token with a proof is bound; calls succeed with matching proofs and fail without one, with another key, a reused `jti`, a wrong `htm` or `htu`, an old `iat` or a wrong `ath`; a bound token sent as `Bearer` is refused.
- An app that requires DPoP gets no token without a proof; an app that does not still gets bearer tokens.
- Refresh with the bound key works, with another key it fails; the MCP endpoint follows the same rules.
- Cross-tenant: a proof and token of another tenant are refused as before.

### Tests (R3 slice 3)
- An administrator reads roles and assignments with pages; a unit administrator sees only their units; apps' roles are left out; no write operation exists; a person without the right, a token without the scope, an application app, an employee's own consent, another tenant and a package without `api.admin` are refused.

### Tests (R3 slice 2)
- A client lists only the prompts its scopes and policy allow, in English and Arabic; arguments that are missing, too long or unknown are refused; a prompt whose tools are not offered is not served; write tools off hides the request prompt; another tenant refuses the token.
