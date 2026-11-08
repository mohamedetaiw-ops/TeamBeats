# ADR-0018: Public API and MCP server: tenant authorization server, token model and MCP hosting

| Field | Value |
|---|---|
| Status | Accepted (2026-11-08 by Mohamed, standing approval to proceed) |
| Date | 2026-11-08 |
| Owners | Mohamed |
| Related specs | 035 (all), 003 (sign-in), 004 (principals), 017 (outbound webhooks), 027 (tool gateway); constitution I, III, V, X, XIV, XVI, XXII, XXVII; ADR-0005, ADR-0009, ADR-0013 |

## Context and constraints
Spec 035 lets outside systems and external AI clients reach TeamBeats through a public REST API and a remote MCP server. Both need OAuth: the MCP authorization model requires an OAuth 2.1 authorization server discoverable from the MCP server's protected resource metadata, with PKCE, resource indicators and client identification through client ID metadata documents. Constitution XXVII requires an ADR for identity protocol changes. The constraints are: tenant identity comes from the session or token and never from client input (I); scopes must never widen permissions (III); no product-held passwords (V); agents and tools act as a user or a narrow service identity (X); one modular monolith (XIV); external APIs are REST with OpenAPI (XVI); new libraries need a reason and an OSI licence (XXII); data access is Npgsql without EF Core (ADR-0009). Identities are per tenant (ADR-0005), and tenants sign in through different providers (Entra ID, Google, SAML, AD FS).

## Decision drivers
Tenant-bound tokens that cannot be replayed across tenants; one consent and revocation model for apps and AI clients; scopes that only narrow; works for every identity provider a tenant uses; no new infrastructure; protocols we do not hand-write.

## Options considered
1. **Tokens from the customer's identity provider.** Each tenant registers TeamBeats as an API in Entra ID and apps get Entra tokens. No authorization server to run, but it works only for Entra tenants, needs per-customer app registrations for every client, cannot express TeamBeats scopes tied to our permission catalogue, and Entra does not support the discovery and client registration that MCP clients use. Rejected.
2. **Built-in per-tenant authorization server on OpenIddict, federating sign-in to the tenant's identity provider (chosen).** OpenIddict is an Apache 2.0 OAuth 2.0 and OpenID Connect server framework for ASP.NET Core, actively maintained, with custom store support so it runs on our Npgsql stores. Users still sign in through their own provider; TeamBeats issues its own short-lived tokens.
3. **External API gateway product (for example Kong or Azure API Management) in front of the monolith.** Gives rate limiting and keys, but adds infrastructure (XV), splits authorization from the policy service, differs between SaaS and on-premises installs (XX), and some editions are commercial. Rejected.
4. **Our own OAuth server code.** No dependency, but token, PKCE, client authentication and metadata handling are easy to get subtly wrong and costly to audit. Rejected.

For MCP hosting:
1. **Official MCP C# SDK, `ModelContextProtocol.AspNetCore` (chosen).** Apache 2.0, maintained by the MCP project with Microsoft, v1.0 released, Streamable HTTP transport in ASP.NET Core, follows protocol revisions.
2. **Hand-written JSON-RPC over HTTP.** No dependency, but we would track every protocol revision ourselves. Rejected.
3. **A separate MCP gateway service in another language.** New deployable and runtime (XIV, XV). Rejected.

## Decision
1. A new `PublicApi` module owns app registrations, credentials, consents, token families, application principal links, event subscriptions, settings and call logs, and hosts the authorization server and the request pipeline for `/api/public` and `/mcp`.
2. Each tenant has its own issuer `https://<tenant host>/oauth` and its own signing keys. Every request checks that the token's issuer and tenant claim match the tenant resolved from the host.
3. The authorization server never authenticates users itself. An authorization request without a TeamBeats session for the tenant goes through the spec 003 sign-in with the tenant's provider, then the consent screen.
4. Tokens: access tokens of 10 minutes by default, bound by resource indicators to either the REST API or the MCP server; rotating single-use refresh tokens with family revocation on reuse; client credentials for application apps with client secrets (hashed) or private key JWT. No static bearer API keys.
5. Scopes are an extra filter. Effective access is the intersection of scopes with the user's permissions (delegated) or with the application principal's roles in `Authorization` (application). `Authorization` gains an `app` principal type and an `assignable_to_apps` role flag.
6. Public endpoints stay in their owning modules, declared with a scope, decision and capability key, and are published in a separate OpenAPI document with its own diff check.
7. The MCP server is hosted in the API process with the official SDK. Tools are spec 027 registry entries marked exposable; every call goes through the `Agents` tool gateway with caller type `external_ai_client`. Only delegated tokens are accepted on MCP.
8. Revocation is enforced by a revocation list keyed by tenant and token family, cached for at most 60 seconds, invalidated by events.

## Consequences and risks
- TeamBeats now runs a security-critical token service: signing key rotation, metadata and refresh token storage become operational duties, covered by the penetration test before R2 GA (spec 026).
- Tokens do not carry TeamBeats permissions, so every call needs a live authorization decision; this is the same cost as the web app and keeps revocation within 60 seconds.
- External AI clients use models outside the tenant's AI Gateway policy. The classification cap and confirmation inside TeamBeats (spec 035 FR-021, FR-022) are the controls; a tenant that cannot accept that leaves external AI access off.
- MCP is still evolving. The SDK absorbs protocol changes; tool contracts are versioned by name (spec 035 section 6).
- If a future mobile app or partner portal needs tokens, it can use the same authorization server (spec 030 may adopt it with its own ADR note).

## Security and multi-tenancy implications
All `PublicApi` tables are tenant-owned with forced RLS except the platform verified client list and caps (listed in `rls-exceptions.md`). Signing keys and credentials are secret manager references or hashes and never logged. Rate limit partitions, revocation caches and idempotency keys include the tenant ID. Cross-tenant token use, app ID reuse, subscription delivery and call log access are mandatory negative tests. Client ID metadata documents are fetched through the spec 017 SSRF guard.

## Rollout and migration plan
R2: module, authorization server and pipeline first, then the scope catalogue endpoints module by module, then the MCP server, then event subscriptions. The internal web API keeps cookie sessions and is unchanged. R3: sender-constrained tokens (DPoP or mutual TLS), admin APIs beside SCIM, MCP elicitation for verified clients. Architecture tests fail if a `/api/public` endpoint has no scope declaration, if a module other than `PublicApi` references OpenIddict or the MCP SDK, or if a role holding an excluded permission is marked assignable to apps.
