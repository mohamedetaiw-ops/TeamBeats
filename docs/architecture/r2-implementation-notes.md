# R2 implementation notes

What was built for R2, slice by slice, and where it differs from the specs and plans. Each difference is either covered by an ADR or listed as a follow-up.

## Connector runtime (spec 017, slice 1: T003, T004)

### Built
- `IConnectorRuntime` in `Integrations.Contracts` for workflows (016, 024) and agents (027): `InvokeAsync` runs inside the caller's request with 3 attempts within 10 seconds; `EnqueueAsync` makes a durable job with 5 attempts over about 30 minutes (delays of 1, 3, 8 and 18 minutes, each with 20% jitter) and publishes `ConnectorActionCompleted` when it ends.
- `IConnectorPack` in the `Integrations` module: a connection kind, a version, its capability key and its actions, each with a side effect, idempotency support and business error codes. Packs are registered in code; connection kinds are now checked against the registered packs instead of a database constraint.
- Idempotency: one run per connection, action and idempotency key. A finished run answers again with its stored outcome and external reference and does not call the system; a running one answers "in progress"; a failed, unknown or refused one runs again.
- Errors: business errors and refused credentials are not retried. A technical error is retried only when it is safe: the attempt never reached the system, the action is a read, or the action is idempotent. Otherwise a write that may have acted stops as "outcome unknown" (FR-010). The system's retry advice (429 `Retry-After`) is honored when it is longer than the backoff.
- Circuit breaker: every attempt is recorded in `integrations.call_attempt`, so every instance sees the same ratio. Half or more failures over the last 20 attempts within 5 minutes opens the circuit, sets the connection to degraded and publishes `ConnectorUnavailable`; after 2 minutes one call is let through, and a success closes it and publishes `ConnectorRestored`. Jobs on an open circuit wait for the half-open time without spending an attempt.
- Rate limit per connection (`rate_limit_per_minute`, default 600): a token bucket with bursts of up to a sixth of a minute's calls. A call inside a request waits up to 1 second; a job waits 5 seconds and tries again.
- A job on a disabled connection, or on a pack outside the package, waits and is looked at again every minute, with the reason as its error class, so a workflow waiting on it pauses rather than fails (US1 scenario 4).
- Run history (`GET /api/v1/integrations/connections/{id}/runs`, permission `integration.operate`) with status, attempts, error class, business code, external reference, counts, duration, correlation ID and replay count, filterable by status (or `dead_letter`) and action, with a cursor. No input or output is ever shown.
- Replay (`POST /api/v1/integrations/connections/{id}/runs:replay`, up to 100 runs): failed and unknown runs from the last 14 days are queued again as jobs with their original idempotency key; the earlier error class stays on the run as `previousErrorClass`; audited as `integration.replay`.
- SSRF guard: `SafeHttp.CreateHandler` is the primary handler of the named HTTP client `connectors`. It resolves the host once, refuses the connection when any address is unspecified, loopback, private, shared (100.64/10), link-local, benchmarking, documentation, multicast or reserved (IPv4 and IPv6, including IPv4 mapped and NAT64 forms), and connects the socket to the checked address, so DNS rebinding cannot reach an internal address. Redirects are not followed. `Integrations:Network:AllowPrivateAddresses` lets an on-premises install reach its own network.
- Host allowlist (`GET`, `POST`, `DELETE /api/v1/integrations/allowlist`, permission `integration.manage`, step-up for changes): exact hosts or `*.example.com`, never IP addresses, at most 200, kept in ASCII form. `HostAllowlist.IsAllowedAsync` accepts only `https` URLs on the list. Custom REST and webhooks will check it before every call.
- Permission `integration.operate` for tenant and integration administrators (migration 0021 adds it to existing tenants). Capability keys of section 9: `integration.connectors`, `integration.custom_rest`, `integration.webhooks`, `integration.onprem_agent` (off in Workplace, on in the other packages) and `integration.max_connections` (0, 10, unlimited).
- `ConnectorJobWorker` runs due jobs per active tenant every 10 seconds and purges once a day: history after 90 days, dead letter inputs after 14 days, attempts after a day. Hosted by the workers host, and by the development host when `Integrations:Jobs:InProcess` is true.
- Telemetry `integration.call`: tenant, connection kind, action, status and latency, never content.
- Tests: `ConnectorRuntimeTests` (15, against a scripted fake ticketing pack: retry and backoff, idempotency, outcome unknown, cut-off attempts, business and credential errors, refusals, rate limit, circuit, jobs, paused jobs, history, replay, retention, allowlist, the HTTP guard, and cross-tenant denial) and `SafeHttpTests` (address and host rules).

### Differences and follow-ups
| Item | Status |
|---|---|
| The plan lists a separate `dead_letter` table | The run row is the dead letter: a failed or unknown run with its input kept for 14 days. One table keeps the original idempotency key and the replay outcome together. |
| Payload content | Inputs are stored only while a run may still run or be replayed and cleared once it succeeds or after 14 days; outputs are returned to the caller and never stored, so a repeated key returns the outcome and external reference without the output. Time-boxed payload capture (FR-007) comes with the run history page (T010). |
| Calls inside a request | Their run rows are written in the caller's transaction. If the caller's request fails after the external call, its run record is rolled back with it. Workflows use jobs for steps that must survive that. |
| Rate limits | Kept per process, so an installation with several API instances allows that many times the limit. The circuit breaker is shared through the database. |
| Half-open circuit | Concurrent calls at the half-open moment may all be let through, not only one. |
| Host allowlist audit | Spec section 13 has no event for allowlist changes; they are audited as `integration.allowlist_changed` (smallest safe default). |
| T001 and T002 | Runs, attempts and the allowlist are migrated; pack actions, webhooks, payload capture and sync agent tables, and the audit actions for credentials, actions, webhook secrets and agents come with their slices. |
| T005 | The Microsoft 365 adapter keeps its own retry until it moves onto the runtime in the next slice. |

## Connection lifecycle and custom REST (spec 017, slice 2: T006, T007 and T012 back end)

### Built
- Packs and their connections (`GET /api/v1/integrations/packs`, `/api/v1/integrations/pack-connections` with create, read, change, `:test`, `:enable`, `:disable`, `dependents` and delete, permission `integration.manage`, step-up for create and change). The catalogue lists every pack with its actions before any credential is entered, marked by what the package allows. Microsoft 365 keeps its own endpoints until T005.
- A connection starts disabled. The administrator saves settings with secret manager references (never values, and only references of the same tenant), tests it, then enables it. The test stores its checks on the connection (`last_test`). Disabling takes effect on the next attempt.
- Audit: `integration.connection_created`, `integration.credential_changed` (any change to the sign-in part of the settings), `integration.connection_changed` (name, rate limit or other settings), `integration.connection_enabled`, `integration.connection_disabled`, `integration.connection_deleted`, `integration.action_defined` and `integration.action_changed`.
- Limits: `integration.max_connections` counts pack connections (10 in Workplace Plus); a connection holds at most 100 actions. Deleting an enabled connection, or one with dependents, needs `confirm=true`.
- Credential expiry: a connection whose credential expires within 14 days becomes `credential_expiring` when it is enabled, changed, or at the daily check of the connector worker, and goes back to healthy when renewed.
- Alerts: `ConnectorUnavailable` (degraded, needs consent, credential expiring; never an administrator's own disable) notifies holders of `integration.manage` in the `integration_alert` category, once per connection and status within an hour, for every connection kind including Microsoft 365.
- Custom REST connector (`custom_rest`, capability `integration.custom_rest`): https base URL on the tenant allowlist (no IP addresses, user names, queries or fragments); sign-in `none`, `api_key` (a header), `bearer`, `basic` or `oauth_client_credentials` (token URL on the allowlist, tokens kept until a minute before they expire and dropped on a 401 or 403). Actions are defined without code (`PUT .../actions/{name}`): method, path, query, headers and a JSON body with `{{input}}` placeholders of declared inputs, output fields as JSON paths, an external reference path, a side effect, an optional idempotency header and declared business errors. Inputs are escaped where they land and undeclared fields are never sent. Answers are read up to 1 MB. 401 and 403 are a refused credential, a declared 4xx a business error, any other 4xx a refused request, 429 and 5xx technical errors retried when safe.
- `actions:test` runs a definition once with sample input before it is saved and records no run (US4 scenario 1).
- Tests: `CustomRestConnectorTests` (9: catalogue and package, lifecycle and call shape, credential and settings audit and delete confirmation, validation and secret references, business errors and retry rules, OAuth tokens, allowlist removal and refused addresses, limits and the tenant boundary, expiry and alerts) against `FakeRestApi`, which replaces the connector HTTP handler in tests; the handler's own address rules stay covered by `SafeHttpTests` and `ConnectorRuntimeTests`.

### Differences and follow-ups
| Item | Status |
|---|---|
| Separate endpoints for pack connections | Microsoft 365 connections keep `/connections` with their features and consent; pack connections use `/pack-connections` until T005 moves Microsoft 365 onto the runtime and the two views merge. |
| Delete confirmation on pack connections | The dependents list names the connection's actions in `enabledFeatures`, the field Microsoft 365 uses for its features, so the Connections area shows one shape. |
| Microsoft 365 in the Connections pages | The pages list pack connections only; Microsoft 365 connections join them with T005. |
| Sign-in methods | OAuth authorization code and mutual TLS certificates (spec section INT-010) are not in the custom REST connector yet; they come with the packs that need them. |

## Connections pages (spec 017, slice 3: T007, T010 and T012 pages)

### Built
- `/admin/connections` (shown when the API lists the Connections section): the tenant's pack connections with status, the pack catalogue with each pack's actions and whether the package includes it, adding a custom REST connection (name, base URL, sign-in method, secret reference, expiry date, rate limit), and the host allowlist.
- `/admin/connections/{id}`: state (status, last success and check, credential expiry, paused calls), the last test with each check, test, turn on and off, settings and credential changes, and delete with confirmation; the action builder for custom REST (method, side effect, path, inputs, query, headers, JSON body, output fields, reference path, idempotency header, business errors, and a test with sample input); run history with status filters, more pages, and replay of selected failed and unknown runs.
- Step-up answers send the administrator to a fresh sign-in and back to the page; refusals show the API's own localized messages. Every text is in English and Arabic, and layouts use logical properties for RTL.
- Acceptance: `tests/Acceptance/specs/connections.spec.ts` (allow a host, add, test, turn on and off, define an action, read run history, delete, in English and Arabic with axe). It calls no external system.

## Microsoft 365 in the Connections area (spec 017, slice 4: T005)

### Built
- `/admin/connections` now lists Microsoft 365 connections first, with status and how many features are on, and adds one by Microsoft tenant ID, name and features. Features the package does not include are shown but cannot be chosen; the API checks again. The consent callback's `?consent=granted|failed` shows as a notice.
- `/admin/connections/microsoft365/{id}`: status, tenant ID and last success; test with a result per feature (users and groups counted, missing permissions, permissions broader than needed); turn the connection on and off; each feature with the Microsoft permissions it needs, whether each is an application or delegated permission, and why in plain words; turn a feature on and off; grant consent for a feature (the existing consent redirect); Mail options (preview line, most messages) and Calendar options (longest range); delete with confirmation.
- Acceptance: a second test in `tests/Acceptance/specs/connections.spec.ts` adds a connection with Mail, reads its permissions and consent link, turns Mail off and on, and deletes it, in English and Arabic with axe. It does not call Microsoft.

### Differences and follow-ups
- The Microsoft 365 adapter keeps its own retry per Graph call site instead of the runtime's run records. Plan 017 allows a retry policy per call site, and Mail and Calendar reads are delegated reads for one employee on every page view: writing a run row for each would fill run history with personal reads that carry no business outcome and cannot be replayed. Directory sync keeps its own job history from spec 003. The API and its rules are unchanged, so the behavior of specs 006 and 018 stays as it was; only the pages are new.

## Webhooks (spec 017, slice 5: T011, T013 back end)

### Built
- A `webhook` pack (capability `integration.webhooks`). A webhook connection is one external endpoint with a signing secret (a secret manager reference), the product events it sends with the fields of each, and optionally an inbound mapping. It has the usual lifecycle: starts turned off, test (host on the allowlist, secret found), turn on and off, rate limit, delete.
- Outbound (FR-013): a handler per product event in `OutboundEvents` (requests submitted, completed and returned; content published; user tasks created and completed; processes completed) enqueues a `deliver` run on every enabled webhook connection that selected the event, with only the selected fields. Delivery is a POST with headers `X-TeamBeats-Delivery` (the idempotency key), `X-TeamBeats-Event`, `X-TeamBeats-Timestamp` (Unix seconds) and `X-TeamBeats-Signature` (`v1=` and the hex HMAC SHA-256 of `{timestamp}.{body}`). Retries, the delivery log, dead letters, replay, the circuit breaker and the rate limit are the runtime's.
- Inbound (FR-014): `POST /api/v1/integrations/inbound/{connectionId}` on the tenant's own host, with the same headers. The tenant comes from the host; the connection must be a turned on webhook connection with inbound set up, otherwise the URL is not found. Signature, then the timestamp window (60 to 300 seconds, default 300), then the delivery ID (kept a day) are checked; each refusal is counted, and `GET /pack-connections/{id}/inbound` shows the counts and the path. An accepted delivery publishes `InboundWebhookReceived` with the target (`event` or `message`), its name, the correlation key for a message, and only the declared fields. Bodies over 1 MB are refused.
- Rotation (FR-015): saving a new secret reference stops the old one at once; with `overlapHours` (1 to 24) the old one keeps verifying inbound and signing outbound (one signature per valid secret, comma separated) until the overlap ends. Other changes keep a running overlap. Audit `integration.webhook_secret_rotated` with the overlap.
- Tests: `tests/Integration/WebhookTests.cs` (signed outbound with selected fields only, retry with the same delivery ID, turned off gets nothing, inbound mapping, refused signature, tampered body, old timestamp and repeat counted, message correlation, body limit, rotation with and without overlap, dual signatures, settings validation, package, tenant boundary).

### Differences and follow-ups
- Spec 024's message correlation is not built yet, so a `message` target is published as `InboundWebhookReceived` for 024 to consume; nothing consumes it today.
- Events that happen while a webhook is turned off, or while the package lacks webhooks, are not kept for later. The spec does not say; not sending them is the smallest safe default.
- Signature headers and format are TeamBeats' own (no standard is named in the spec). Outbound events carry identifiers and states only; fields that name a person are not offered.
- The inbound route is listed as reachable without a session in the security suite, because other systems authenticate with the signature.

## Webhook pages (spec 017, slice 6: T013 pages)

### Built
- `/admin/connections` adds a webhook: name, the address that receives events, the signing secret reference, the events to send with their fields (from `GET /api/v1/integrations/webhook-events`), inbound deliveries (event or process message, name, correlation key path, fields to keep, timestamp window) and the rate limit.
- `/admin/connections/{id}` for a webhook connection: the same settings, with the rotation overlap asked only when the secret reference changes; the inbound address on the tenant's host with accepted and refused counts by reason. Test, turn on and off, run history (the delivery log) and delete are the shared ones.
- Acceptance: a third test in `tests/Acceptance/specs/connections.spec.ts` adds a webhook with an event and inbound deliveries, reads the inbound address and deletes it, in English and Arabic with axe.

## On-premises sync agent, server side (spec 017, slice 7: T014, T015 part 1)

### Built
- An `onprem_ad` pack (capability `integration.onprem_agent`). Settings are the domain controller host, the port (636 for LDAPS or 3269 for the global catalogue over LDAPS, default 636), the base DN and optional user and group LDAP filters. The service account and its password stay with the agent in the customer network. The pack has no actions TeamBeats calls; its test checks that an agent is enrolled and that one was in touch in the last 5 minutes.
- Enrolment (FR-016): an administrator makes a one-time code (step-up) that expires in 24 hours; only its SHA-256 hash is kept and a new code replaces an unused one. The agent posts the code, a name and the public half of an ECDSA P-256 key it made to `POST /api/v1/agent/enrol` on the tenant's own host. That binds the agent to the tenant and the connection; audit `integration.agent_enrolled` with the key thumbprint.
- Every later agent request names the agent (`X-TeamBeats-Agent`) and is signed with its private key over method, path, timestamp and the SHA-256 of the body (`X-TeamBeats-Agent-Timestamp`, `X-TeamBeats-Agent-Signature`). The timestamp must be inside 5 minutes and not older than the newest one already accepted. Revoking an agent (step-up, audit `integration.agent_revoked`) refuses its very next request, which is well inside the 1 minute the spec asks, and puts a job it held back in the queue.
- Jobs (FR-017): an administrator queues a directory sync (one waits or runs per connection); agents pull with `POST /api/v1/agent/jobs:pull`. A per-connection lock and a 10 minute lease mean one agent at a time holds the connection's job; records and pulls by the holder extend the lease. A lapsed lease puts the job back, and after 3 attempts it fails as `not_responding`. Records come in batches of at most 1,000 users and 1,000 groups in the canonical directory form and are kept per job; completing publishes `AgentJobFinished` with the counts.
- Status: on every worker tick an enabled AD connection whose enrolled agents have all been quiet for 5 minutes becomes degraded and its owner is told (`ConnectorUnavailable`); it is healthy again once an agent is back.
- Tests: `tests/Integration/SyncAgentTests.cs` (one-time and expiring codes, only the hash kept, one key per agent, key type, signature, tampered body, window and replay, revocation at once, one taker with two agents, record limits, completion, lapsed leases, offline status and test checks, settings validation, package, tenant boundary). The five agent routes are listed as reachable without a session in the security suite.

### Differences and follow-ups
- Plan 017 says gRPC over TLS with client certificates. Staging and production sit behind Fly.io, which ends TLS at its proxy, so a client certificate never reaches TeamBeats. The agent instead proves itself with a key pair bound at enrolment and signs each request, over plain HTTPS on the tenant's host. That keeps the same properties (outbound only, bound to one tenant and connection, revocable at once) and needs no gRPC dependency. The agent still talks outbound only and long-polls by pulling.
- Next slices: the agent worker itself (a .NET worker reading AD over LDAPS with `System.DirectoryServices.Protocols`), spec 003 directory sync reading `AgentJobFinished` and the records, and the agent pages in the Connections area.

## Active Directory into the directory (spec 017, slice 8: T015 part 2, with spec 003 US6 for AD DS)

### Built
- Directory sync settings (`PUT /api/v1/tenant/directory`) take an optional `connectionId` of an enabled on-premises AD connection; without it they are for the Microsoft 365 connection as before. The settings show `source` (`entra` or `adds`).
- A due AD run asks the sync agent for a read through the new `IAgentDirectory` contract and waits; no second run starts while a read waits or runs. When `AgentJobFinished` arrives, Identity reads the job's records through the same contract and applies them with the same rules as Entra ID: users keyed by the AD `objectGUID`, never by email; disabled or out of scope accounts lose access; the deletion threshold; selected groups kept as directory groups (keys `ad:{objectGUID}`). A failed read changes nothing and counts toward the alert after 3 failures. A read an administrator starts from the Connections area is applied as its own run.
- Integrations keeps the read position: each job carries the cursors (AD `uSNChanged` values) of the last succeeded job, and the agent reports the next ones when it completes.
- Tests: `tests/Integration/OnPremDirectorySyncTests.cs` (create by objectGUID, cursors carried to the next read, disable, groups, administrator read, failed read, settings only for a usable AD connection of the same tenant, a read never applied in another tenant).

### Differences and follow-ups
- AD FS sign-in (spec 003 US5) is not built yet, so synced AD accounts are linked under the issuer `urn:teambeats:onprem-ad:{connectionId}` with no identity provider. AD FS sign-in will match its token's `objectGUID` to that issuer for the connection. Until then synced AD users appear in the directory, groups and organization mapping, but cannot sign in.
- Group to role mapping stays with the Entra ID identity provider settings, so it does not apply to AD groups yet; it comes with AD FS.
