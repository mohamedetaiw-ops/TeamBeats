# Tasks: Public API, MCP Server and App Access

Depends on 003, 004, 005, 006, 014, 015, 016, 017 (outbound webhook runtime), 026, 027. R2.

## Phase 1: Module and authorization server
- [x] T001 `PublicApi` and `PublicApi.Contracts` projects; module map entry; migration for the section 8 tables with forced RLS; platform tables listed in `rls-exceptions.md`
- [x] T002 Permissions `api.manage`, `api.view`; capability keys of section 9 in the spec 005 seed; audit actions of section 13
- [x] T003 [P] Tests: per-tenant issuer and keys; host to issuer check; resource-bound audience; PKCE required; no implicit or password grant; refresh rotation and reuse detection; cross-tenant token and app ID refusal
- [x] T004 OpenIddict server on Npgsql stores; sign-in through the spec 003 flow; consent page; token, revocation and metadata endpoints; per-tenant signing keys from the secret manager

## Phase 2: User Stories 1 and 2, apps, principals and consent (P1)
- [x] T005 [P] [US1] [US2] Tests: scope narrowing property test; app-assignable role rule; org boundary for an application principal; user versus admin consent; guest refusal; revocation within 60 seconds; membership disable at once
- [x] T006 [US1] App registration, credentials with step-up, expiry alerts and rotation; `app` principal type and `assignable_to_apps` flag in `Authorization`
- [x] T007 [US2] Delegated flows, consent records, connected apps page, admin consent list

## Phase 3: User Story 5 and the public API pipeline (P1)
- [x] T008 [P] [US5] Tests: rate limit partitions and 429 headers; monthly quota at 80% and 100%; tenant may only lower limits; tenant switch off within 60 seconds; IP ranges
- [x] T009 [US5] Request pipeline (network rules, token validation, revocation cache, entitlements, rate limits, call log); usage page
- [x] T010 `PublicEndpoint` convention, architecture test for scope declarations, separate OpenAPI document and diff check in CI; idempotency store; problem details, cursors, ETags
- [ ] T011 Public endpoints for the R2 scope catalogue in their owning modules (009, 010, 002, 011, 012, 014, 016, 025), each with cross-tenant, org boundary and not-found tests (done: 002, 009, 010, 011, 025; open: 012, 014, 016)

## Phase 4: User Story 3, MCP server (P1)
- [ ] T012 [P] [US3] Tests: client allowlist and verified metadata; tools list per policy, entitlement and scope; classification cap fixtures; pending confirmation then act; expiry after 15 minutes; execution records; kill switch within 10 seconds; no application tokens on MCP
- [ ] T013 [US3] External AI caller type, exposable flag and external AI client policy in `Agents` (spec 027)
- [ ] T014 [US3] MCP server with Streamable HTTP, protected resource metadata, read tools, resources, `ask_teambeats` and confirmed write tools; confirmation card in the task inbox and notification centre
- [ ] T015 [US3] Spec 034 suite cases for the external AI caller type (permission isolation, classification, unsafe actions)

## Phase 5: User Stories 4, 6 and 7 (P1, P2)
- [ ] T016 [P] [US4] Tests: thin payloads; filtered notifications for hidden resources; signature; pause after 3 days; catalogue only
- [ ] T017 [US4] Public event schemas, subscription management, delivery through `IWebhookDelivery`
- [ ] T018 [US6] Emergency controls: disable app, revoke tokens, tenant switches, platform block; security alerts in spec 026 US5
- [ ] T019 [US7] Developer page with OpenAPI, catalogues, MCP tool list and metadata URLs; deprecation headers

## Final phase
- [ ] T020 Admin and employee pages in Arabic and English with axe; e2e journeys; performance tests for section 15; SIEM stream fields; R2 implementation notes; spec evidence
