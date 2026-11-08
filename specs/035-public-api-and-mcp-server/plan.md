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
