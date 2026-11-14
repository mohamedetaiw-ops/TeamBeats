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
| Payload content | Inputs are stored only while a run may still run or be replayed and cleared once it succeeds or after 14 days; outputs are returned to the caller and never stored, so a repeated key returns the outcome and external reference without the output. Time-boxed payload capture (FR-007) is built in slice 13. |
| Calls inside a request | Their run rows are written in the caller's transaction. If the caller's request fails after the external call, its run record is rolled back with it. Workflows use jobs for steps that must survive that. |
| Rate limits | Kept per process, so an installation with several API instances allows that many times the limit. The circuit breaker is shared through the database. |
| Half-open circuit | Concurrent calls at the half-open moment may all be let through, not only one. |
| Host allowlist audit | Spec section 13 has no event for allowlist changes; they are audited as `integration.allowlist_changed` (smallest safe default). |
| T001 and T002 | Runs, attempts and the allowlist are migrated here; pack actions, webhooks, sync agents and payload capture came with their slices (2, 5, 7 and 13), and so did the audit actions. |
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

## The sync agent program (spec 017, slice 9: T015 part 3)

### Built
- `src/SyncAgent`: the agent the customer installs. `enrol --server --code --name` makes an ECDSA P-256 key, enrols with the code and keeps the private key in its state folder (owner read and write only on Linux). `run` (a Windows service, a systemd service or the container in `deploy/docker/sync-agent.Dockerfile`) sends a heartbeat every minute, pulls every 30 seconds and runs directory sync jobs. Install and operation steps: `docs/operations/sync-agent.md`.
- The read uses System.DirectoryServices.Protocols over LDAPS only, with the operating system's certificate checks, paged searches and ranged reads of large groups. The first read takes every user and group under the base DN; later reads take entries with `uSNChanged` after the cursor plus tombstones of deleted users and groups. The cursor is the domain controller's highest committed USN taken before the read. Users are keyed by `objectGUID`; `userAccountControl` decides whether the account is enabled; the manager DN is turned into the manager's `objectGUID`.
- A changed group is sent with its whole membership; a new `MembersComplete` flag on the canonical group record makes Identity replace the members it knew, so membership stays right after an agent is replaced.
- LDAP failures become plain error classes (`credential_refused`, `not_responding`, `server_error`, `credential_missing`). The service account and its password come from the agent's settings or environment and are never sent or logged.
- Tests: `tests/Integration/SyncAgentProgramTests.cs` runs the program's client and job runner against the real API with a directory played by the test (enrol, key file mode, first read, manager resolution, group members, incremental filter after the cursor, disabled user, deleted group, password never stored); `tests/Unit/SyncAgentMappingTests.cs` covers the mapping.

### Differences and follow-ups
- New packages, all Microsoft and MIT licensed: `System.DirectoryServices.Protocols` (named in plan 017), `Microsoft.Extensions.Hosting.WindowsServices` and `Microsoft.Extensions.Hosting.Systemd` to run as a service. On Linux the agent needs the OpenLDAP client library (`libldap`), which the container image installs.
- A signed Windows installer and code signing of the agent are release work and are not in this slice.
- Objects moved out of the base DN are not seen as removed until they are deleted or disabled in AD.

## Sync agent pages (spec 017, slice 10: T015 pages)

### Built
- `/admin/connections` adds an on-premises Active Directory connection: name, domain controller, port (636 or 3269), base DN and optional user and group filters. The page says that the service account is set on the agent's machine, not here.
- `/admin/connections/{id}` for an AD connection: the same settings; the sync agents section makes a one-time enrolment code (shown once with its expiry), lists the enrolled agents with whether each is in touch, its last contact, version and key thumbprint, and revokes one after confirmation; the directory reads with their state and counts; "Read now"; and "Use for directory sync", which points directory sync (spec 003 US6) at this connection. The test checks of the connection show whether an agent is enrolled and in touch.
- Acceptance: a fourth test in `tests/Acceptance/specs/connections.spec.ts` adds an AD connection, makes an enrolment code and deletes it, in English and Arabic with axe.

### Differences and follow-ups
- There is no directory sync settings page yet (spec 003 US6 scope, schedule and threshold are set through the API), so the AD connection page offers only the switch to use it for directory sync.

## Connector actions as agent tools (spec 017, slice 11: T008 and T009, agent tool path; spec 027 FR-006)

### Built
- Actions declare their inputs (`ConnectorInput`: name, type, required). Custom REST actions take theirs from the definition; a pack with fixed actions declares them in code. An action with no declared inputs, such as a webhook delivery, can be neither an agent tool nor a route step.
- `integrations.connector_tool` (forced RLS): the integration administrator registers an action of a connection as the tool `connector.<name>`, with a description for the model and a title per language for people (`/api/v1/integrations/pack-connections/{id}/tools`, `integration.manage`; registering needs step-up, like defining an action). Each change is audited as `integration.action_changed` with the tool name, since spec 017 section 13 names no separate tool event.
- `IAgentToolSource` in `Agents.Contracts`: the tool registry now holds the product tools declared in code and the tenant's own tools, loaded once per scope when the agent policy is read. A product tool always wins a name clash. The parameters of a connector tool are exactly the action's declared inputs (`additionalProperties: false`), read from the action each time, so a changed action never leaves a stale schema.
- Reads are `read` tools that run without asking; writes and external effects are `write_external` tools whose default rule is "confirm", which need `ai.agents.write_tools` and L3. The tool gateway of spec 027 runs every check first; the tool then sends only the declared inputs through the connector runtime with a key per run and parameters for writes, and the run is correlated as `agent-run:{runId}` in run history. A business error goes back to the model as the system's refusal with its code; an unknown outcome, a disabled or missing connection and a failure go back as short keys.
- `/admin/connections/{id}` has an "Agent tools" section that lists, registers and removes tools; the agent editor and the policy editor show a tenant tool by its title. Acceptance: the custom REST test registers the new action as a tool.
- Tests: `tests/Integration/ConnectorToolTests.cs` (registration rules and audit, cross-tenant denial, only validated declared parameters reach the system, the run in run history, a disabled connection turns the tool off, a write tool needs L3 and the policy can block it).

### Differences and follow-ups
- Connector write tools wait for L3 (spec 027 R2), which is not built yet, so they can be registered and governed by the policy but no agent can hold one today. The incident for an unknown write outcome (spec 027 edge cases) waits for the Agent Security Center incidents; until then the unknown run is in the connection's dead letters.
- Workflow and engine steps that call connector actions (T009 for 016 and 024) are slice 12.

## Connector steps in service routes (spec 017, slice 12: T008 and T009 for specs 016 and 024)

### Built
- Routes of the process engine take service tasks: a `RouteStep` with a `ConnectorStep` (connection, action, input mapping, business error paths) compiles to a BPMN `serviceTask` with a `teambeats:connector` extension and reads back unchanged. Publishing checks each step against the action: it must exist on a tenant connection, declare its inputs, have every required input mapped and no undeclared one, and map only declared business errors.
- The engine runs a step as a durable connector job (`EnqueueAsync`) with the key `wf:{instance}:{step}`, fixed per instance and step, so a retry or a replay never acts twice where the system supports it. Only the instance variables the step maps are sent. `workflow.service_task` and `workflow.incident` (forced RLS) keep the state. Success moves to the next step; a business error with a path continues or ends the instance as rejected; anything else (failure after retries, outcome unknown, a business error with no path) raises an incident and the instance waits. An operator retries (same key), skips or cancels with a reason; each is audited (`process.incident_raised`, `_retried`, `_skipped`, `_cancelled`) and a second resolution is refused with 409. Withdrawing an instance closes its running steps and open incidents.
- Services: a form service's route has up to 5 automatic steps after the approval (`ServiceRoute.Actions`). The request passes the values a step may map: `form.{field}`, `request.number`, `requester.name`, `requester.email`. After the approval the request is "approved" and can no longer be cancelled by the requester; the end of the route closes it as completed, rejected or cancelled. Tracking shows "automatic step done" with the external reference, "automation failed", and the owner's retry, skip or cancel with the reason. Owners (`service.manage` at the owning unit) resolve through `POST /api/v1/requests/{id}/automation/incidents/{incidentId}:resolve`.
- `workflow.automation` is a new capability (Workplace Plus and above, spec 016 section 9); saving or publishing a route with automatic steps needs it. `GET /api/v1/services/admin/connector-actions` lists the actions an owner can use (names and fields only).
- Pages: the service builder has an "Automatic steps" section (action, names in both languages, a request value per field, a path per business error); the request page has an "Automatic steps" section with each step's state, reference, and retry, skip and cancel for owners. Acceptance: `specs/service-automation.spec.ts`.
- Tests: `tests/Integration/ServiceAutomationTests.cs` (mapped fields only, idempotent retry after a timeout with one key, business error paths, incident then retry, skip and cancel with 409 on a second resolution, publish checks, package and tenant boundaries, the designer list); `tests/Unit/RouteCompilerTests.cs` (BPMN round trip and step validation).

### Differences and follow-ups
- Spec 016 US8 scenario 5 alerts the service owner when a step fails. That alert is a follow-up; owners see the failed step on the request and in the connection's run history. Spec 024 names `process.incident_raised`, `_retried` and `_skipped`; `process.incident_cancelled` is added for the cancel path.
- The route stays approval then automatic steps in order. Other step types of US8 (set field, notify, wait, branch, parallel, start another service), triggers and test mode come with the visual designer.
- A step's name in Tracking is the preferred language at publish time, like the approval step; the request page shows each automatic step in the reader's language.

## Payload capture for debugging (spec 017, slice 13: T001 and T002 close)

### Built
- `integrations.connection.capture_until` and `integrations.run_capture` (forced RLS). Capture is off by default. An administrator (`integration.manage`, step-up) turns it on for one connection for 1 to 24 hours with `PUT /api/v1/integrations/connections/{id}/capture` and must state that the connection carries Public or Internal data only (spec 017 section 8); `DELETE` turns it off early. Both are audited as `integration.connection_changed` with the window, since section 13 names no separate event.
- While the window is open, each run that finishes keeps its input and the system's output, up to 64 KB each, for 24 hours. The connector worker deletes expired captures on every tick, so nothing outlives its 24 hours by more than a few seconds. Run history marks a captured run; its content is read separately with `GET /api/v1/integrations/connections/{id}/runs/{runId}/capture` (`integration.operate`). Run history itself still shows no content.
- Microsoft 365 and on-premises directory connections cannot capture, because they carry mail, calendar and personal directory data.
- Pages: the run history section of a connection has a "Payload capture" control (window, the data statement, turn on and off) and shows what a captured run sent and received. Acceptance: the custom REST test turns capture on and off.
- Tests: `tests/Integration/PayloadCaptureTests.cs` (off by default, a window keeps input and output for operators only, turning off stops capture, deletion after 24 hours, window and statement rules, refused kinds, employee and cross-tenant denial).

### Differences and follow-ups
- Spec 017 section 9 lists `integration.erp_packs`; Oracle and SAP keep the separate keys `integration.oracle_fusion` and `integration.sap` that spec 005 seeded in R0, so no new key is added until those packs are built.
- The classification of a connection's data is the administrator's statement at capture time; connections carry no classification of their own yet.

## Ticketing packs (spec 017, slice 14: T017)

### Built
- Two first-party packs, as the section 12 default says (other ticketing systems use custom REST). Both use the framework key `integration.connectors`, since section 9 gives packs in the package no key of their own. Settings are the instance or site address (https, on the tenant allowlist) and a sign-in with a secret manager reference; the REST plumbing (allowlist, sign-in, OAuth client credential tokens, status mapping, 1 MB answers) is shared with custom REST in `CustomRest/RestSignIn.cs`.
- ServiceNow (`servicenow`, sign-in `basic` or `oauth_client_credentials`): `create_incident` (write, inputs `short_description` required, `description`, `caller`, `category`, `urgency`; output `number`, `sysId`, `state`; reference the incident number; business error `invalid_request` on a 400) and `get_incident_status` (read, input `number`; business error `not_found`). Each incident carries a correlation ID that is a hash of the idempotency key, and the pack looks it up before creating, so a retry after a timeout returns the incident ServiceNow already made (US2 scenario 2). The action is therefore idempotent.
- Jira Service Management (`jira_service_management`, sign-in `basic` with the account email and an API token, or `bearer`): settings also name the service desk and request type. `create_request` (write, inputs `summary` required, `description`, `on_behalf_of`; output `issueKey`, `issueId`, `status`; reference the issue key; business error `invalid_request`) and `get_request_status` (read, input `issue_key`; business error `not_found` on a 404). The service desk API takes no idempotency key, so `create_request` is declared not idempotent and a timeout after sending stops with "outcome unknown" for the operator.
- Test connection checks the host, the credential, a token for OAuth, and then one cheap read against the system (`integrations.test.system_ok`, `system_refused`, `system_failed`).
- Pages: the Connections area has an add form for each ticketing pack in the package, with only that pack's sign-in methods; the connection page edits the same settings.
- Tests: `tests/Integration/TicketingPackTests.cs` (declared fields only, canonical output, the ServiceNow retry finding its incident, business, credential and input errors, settings rules, package and cross-tenant denial). Acceptance: the connections test adds a ServiceNow connection in English and Arabic with axe.

### Differences and follow-ups
- Record numbers and issue keys are checked as letters, digits, `_` and `-` before they reach a query, so input can never change a ServiceNow encoded query.
- The ServiceNow caller is sent as given (a user sys_id or user name). Mapping a TeamBeats person to a ServiceNow or Jira user is a follow-up with the directory contract.

## Slack pack (spec 017, slice 15: T018; spec 015 US8 scenario 3)

### Built
- A `slack` pack (framework key `integration.connectors`). Settings are the Web API address (default `https://slack.com/api`, on the tenant allowlist) and the bot token as a secret manager reference. Its actions (`lookup_user`, `send_direct_message`, `list_channels`, `share_link`) declare no inputs, so they are never route steps or agent tools; only notifications and content sharing call them, through the runtime, so each call is in run history under the rate limit and circuit breaker. Slack's `ok: false` errors map to the plain classes (credential, throttled, server, `user_gone`, `channel_unavailable`). Text is escaped so it can never become a mention or a hidden link, and links are always https addresses on the tenant's own host. Slack connections cannot capture payloads.
- Linking: a person links their Slack account from their notification preferences. The Slack member is found by the person's work email (`users.lookupByEmail`) and kept in `integrations.slack_link` (forced RLS). Unlinking, deleting the connection, or Slack answering that the member left all drop the link. A tenant uses its oldest enabled Slack connection.
- Notifications: a Slack choice per category (`notifications.preference.slack`, off by default). Mandatory messages go to Slack too while the person is linked. Slack deliveries follow quiet hours, never join the digest, and retry with the email backoff for up to 2 hours (spec 015 section 9). When the person left the workspace, the delivery is dropped as `slack_user_left`, the link is removed, and the message goes by email if email did not already have it (spec 017 US8 scenario 2).
- Sharing: a communicator (`content.publish` at the item's site) shares a published item to a public channel from the item page, with an optional note. Only Public or Internal items aimed at everyone can be shared, because channel members are not checked against the audience. Only the title, the note and the link are posted, and sharing is audited as `content.shared`.
- Pages: Slack connection form in the Connections area, Slack column and link control in notification preferences, and a "Share to Slack" panel on a live item.
- Tests: `tests/Integration/SlackTests.cs` (direct messages for linked people per category, run history without content, unlinking, fallback to email, linking refused outside the workspace, disabled connection, no capture, sharing rules, employee and cross-tenant denial).

### Differences and follow-ups
- Linking matches the work email in the workspace instead of a Slack sign-in, since spec 017 does not say how accounts are linked. Sign in with Slack can replace it without changing the stored link.
- A share that Slack refuses is rolled back with the request, so it does not count towards the connection's circuit breaker.
- SMS for mandatory messages (spec 015 R2) is a separate connector and is not part of this slice.

## Clock data import (spec 017, slice 16: T016 clock data; spec 022 dependency)

### Built
- A `clock_import` pack (framework key `integration.connectors`). A connection reads clock data from an SFTP server once a day, or takes CSV files an administrator uploads. Both use the same CSV layout: separator, header row or not, a column per field (employee number, punch ID, date and time or separate date and time, direction, device; by heading or by number), .NET date and time formats or ISO 8601, the source's IANA time zone for times without an offset, and the source's words for in and out.
- SFTP uses SSH.NET (MIT, as the plan names; the base library has no SFTP client) behind the `IClockFileSource` port. The host must be on the tenant allowlist, every resolved address is checked as `SafeHttp` checks HTTP, and the server's host key must match the SHA256 fingerprint pinned in the settings before the credential (a password or private key, as a secret manager reference) is sent. TeamBeats only lists and reads; it never moves or deletes files. Each file is imported once, keyed by name, size and change time; up to 20 files and 50 MB per file per run.
- Every import is a job of the connector runtime (`import_clock_data`, a read, idempotent, no inputs, so never a route step or tool), so it is in run history with retries, replay and the circuit breaker. The daily import is queued once per local day after the configured time (default 02:00); an administrator can also import now. Uploads (raw CSV body, up to 20 MB, audited as `integration.clock_data_uploaded`) are stored until imported, then their content is cleared; the same content uploaded twice is refused.
- Rows map to people by employee number through the new People contract `IEmployeeNumbers` (active people of the tenant, under the field's configured source; a number held by two people matches nobody). Punches are stored as canonical clock events in `integrations.clock_punch`, once per source punch ID; a row without a punch ID gets one hashed from employee, time and direction. Rows not imported are listed per file with line, employee number as written and reason (`unknown_employee`, `missing_employee`, `bad_time`, `bad_direction`, `too_long`); no other row content is kept. All tables have forced RLS.
- Spec 022 reads punches through `IClockData.ReadAsync(afterSequence, limit)` and is told of each file by the `ClockDataImported` event.
- Pages: an add form for clock data in the Connections area, the same settings on the connection page, and a clock data files panel with upload, import now, counts per file and the rows not imported.
- Tests: `tests/Unit/ClockCsvTests.cs` (mapping by heading and by number, time zones, daylight saving gaps, quoting, bad rows, file patterns) and `tests/Integration/ClockImportTests.cs` (daily SFTP import once a day, rows mapped inside the tenant only, a person of another tenant unmatched, punches stored once, uploads, refusals, host key, credential and down server, settings rules, package, employee and cross-tenant denial, and 50,000 punches within one minute against the spec 022 target of 10 minutes). Acceptance: the connections test adds an upload connection, uploads a file and reads the row not imported, in English and Arabic with axe.

### Differences and follow-ups
- Retention is not in the spec: punches are kept 400 days for attendance history, uploaded files and the rows not imported 90 days, and SFTP file records as long as punches so a file is never read twice.
- The SFTP adapter is covered by a fake in CI, since the hosted runners have no SFTP server; the SSH.NET code path is exercised only against a real server.

## Finance hand-off (spec 017, slice 17: T016 finance hand-off; spec 022 dependency)

### Built
- A `finance_rest` pack (framework key `integration.connectors`) for any finance system with a REST API. Settings: the https address on the allowlist, the sign-in (API key, bearer token, user name and password, or OAuth client credentials, as secret manager references), the submit path and the status path (with `{reference}`), where each claim field goes in the request body (dotted names make nested objects; only mapped fields are sent), the optional lines field and its line fields, where the reference and the status are in the answers, the finance system's words for submitted, approved, paid and rejected, and an optional idempotency header.
- Two actions run through the connector runtime, so both are in run history with retries, replay and the circuit breaker: `submit_expense` (a write; idempotent only when an idempotency header is set, keyed per hand-off attempt) and `get_payment_status` (a read).
- Spec 022 hands a claim over through the new Integrations contract `IFinanceHandOff.SubmitAsync` and reads its state with `GetAsync`. The oldest enabled finance connection of the tenant is used; with none, or outside the package, the answer is `unavailable` and nothing is stored. Each hand-off is a row in `integrations.finance_handoff` (forced RLS) with the state (`queued`, `sent`, `failed`, `unknown`), the external reference and the payment status. A failed or unknown hand-off can be sent again as a new attempt; the outcome of an older attempt never overwrites a newer one. `FinanceHandOffChanged` is published on every change of state or payment status.
- Status checks run every six hours for each sent claim until it is paid or rejected.
- Pages: an add form for finance in the Connections area, the same settings on the connection page, and a panel of the claims handed over through the connection with reference, payment status and last check. Claim content is not shown there and payload capture is not offered on a finance connection, since claims carry personal finance data.
- Tests: `tests/Integration/FinanceHandOffTests.cs` (only mapped fields sent with the idempotency key, the reference stored, status read until paid, a refused claim failing with its business error and sent again, a timeout without an idempotency header stopping as unknown, settings rules, claim checks, no capture, package and cross-tenant denial). Acceptance: the connections test adds a finance connection with mapped fields and sees no claims yet, in English and Arabic with axe.

### Differences and follow-ups
- Without an idempotency header a submit is not idempotent, so a timeout after sending stops as `unknown` for an administrator to check, rather than risking a second claim in the finance system.
- The six hour status check interval is not in the spec; it is the smallest safe default for a payment status that changes once or twice a week.

## Google Workspace directory and Drive (spec 017, slice 18: T019; FR-019, INT-004; spec 003 US6)

### Built
- A `google_workspace` pack (framework key `integration.connectors`). Settings: the primary domain, the administrator TeamBeats reads as, the service account email and its key as a secret manager reference (the key file Google gives, or the PEM private key), an optional customer ID, the features in use (`directory`, `drive`) and, for Drive, up to 50 shared drive IDs. `oauth2.googleapis.com`, `admin.googleapis.com` and, for Drive, `www.googleapis.com` must be on the allowlist. The pack declares no actions, so nothing is a route step or an agent tool, and payload capture is not offered.
- Sign-in is the JWT bearer grant (RFC 7523) with domain-wide delegation, signed RS256 with the base library; no Google client library is added. Tokens are cached per tenant, connection, user and scope until a minute before they expire, and dropped when Google refuses them. Test connection checks the hosts, that the key parses and belongs to the service account, a directory token, a Drive token and every listed shared drive.
- Directory: the new Integrations contract `IPackDirectory` is read by spec 003 directory sync as the source `google` (Identity migration 0009). Google has no change feed for users, so every read lists all users, groups and user members and is marked complete in `DirectoryChanges.Complete`; Identity then removes from its mirror whatever Google no longer lists, and the deletion threshold stops a short list. Users are keyed by their Google ID, which is the `sub` of Google sign-in, under the issuer `https://accounts.google.com`. Suspended and archived accounts are disabled. Profile fields come from the primary organization, work and mobile phones, the `organization` external ID and the manager relation (given by email, passed on as the manager's ID). Group IDs for selected groups and the user scope are Google's own IDs for this source.
- Drive: the document repository contract of ADR-0016 now exists in `Integrations.Contracts` (`IDocumentRepository` with canonical container, item, permission, change and content types) as the extension point for spec 011 connected libraries. Google Drive is its first adapter: containers, folder listing, one item with its permissions mapped to read, write and owner, changes per shared drive with a cursor, and content opened as the user (Google Docs, Sheets and Slides as PDF, up to 100 MB). Nothing outside the listed shared drives is ever returned, and an ID that is not a Drive ID is never sent to Google.
- Pages: an add form in the Connections area, the same settings on the connection page, and a button to use the connection for directory sync when the directory feature is on.
- Tests: `tests/Integration/GoogleWorkspaceTests.cs` (users by Google ID with manager, suspended users left out, selected groups, a removed user disabled by the next complete read, Google down or refusing changes nothing, a short list stopped by the threshold, Drive inside the listed drives only, permissions, changes, content as the user and as PDF, a user outside the Workspace refused, settings rules, a bad key reported without calling Google, no capture, package and cross-tenant denial). Google is a fake that checks each assertion's signature with a key the test makes. Acceptance: the connections test adds a connection with both features and sees the test stop at the missing key, in English and Arabic with axe.

### Differences and follow-ups
- Spec 011 connected libraries (US9, US10) are not built yet; they will read Google Drive and SharePoint through this contract. The SharePoint adapter joins it then, behind a router by connection kind.
- Directory and Drive reads call Google directly, as Microsoft Graph reads do, so they are not in run history; failures are kept as the directory sync run reason or the repository's unavailable reason.
- Google sign-in (spec 003 T018) is not built yet; synced Google accounts are ready for it under the Google issuer.
- Members of nested groups are not expanded, as with Entra ID.

## Connector-down suite and performance (spec 017, slice 19: T020; SC-001, section 15)

### Built
- `tests/Integration/ConnectorDownTests.cs`, marked `Suite=ConnectorDown`, runs the five cases of SC-001 against two packs (custom REST and Slack): a revoked credential fails at once without retries and replays once fixed, a throttling system is waited for as it asks, a system that is down is retried, left as a dead letter and opens the circuit, a partial failure finishes each call on its own, and a duplicate request with the same key reaches the system once. It runs in the integration suite on every build.
- `tests/Integration/ConnectorPerformanceTests.cs`: connector overhead at p95 under 30 ms (the fake system answers in process, so the time is an upper bound on the overhead) and 1,000 queued jobs within a minute.
- `OnPremDirectorySyncTests.Twenty_thousand_users_sent_by_the_agent_are_applied_well_within_thirty_minutes` sends 20,000 users through the agent endpoint and asserts under 10 minutes (about 2 minutes locally).
- `ArchitectureTests.Connectors_never_call_models` (FR-024): the Integrations module never references the AI Gateway contracts.

### Differences and follow-ups
- The job worker ran at most 50 jobs per tenant per 10 second tick, about 300 a minute, below the section 15 budget. It now drains: a tenant whose batch was full gets more batches in the same tick, inside an 8 second budget (`ConnectorJobs.BatchSize`, `RunBatchAsync`, `ConnectorJobWorker.DrainAsync`).

## KPI feed (spec 017, slice 20: T021; FR-022)

### Built
- An inbound webhook with the target `kpi` maps its body to the canonical fields `metric`, `value`, `unit`, `period_start`, `period_end`, `source_time` and the optional `location`. Saving refuses a mapping that misses a required field or names any other (`validation.webhook.kpi_fields`). The signature, window and delivery ID checks of the webhook stay the same.
- Each delivery carries one reading. Times are ISO 8601 with an offset, or dates read as midnight UTC; a source time more than 5 minutes ahead, an end before the start, a value that is not a number or text over its length is refused with `validation.webhook.kpi_reading` and the field.
- Readings are kept in `integrations.kpi_reading` (migration 0013, forced RLS), one per connection, metric, location and period. A reading measured later replaces the value and an older one is ignored. A stored reading publishes `KpiReadingReceived`; no `InboundWebhookReceived` is published for KPI deliveries. `IKpiFeed.ReadAsync` gives consumers the readings under the `integration.connectors` key. Readings are purged after 400 days.
- Pages: the webhook form offers the target "KPI readings" with its own field hint.
- Tests: `tests/Integration/KpiFeedTests.cs` (stored once per period, later replaces, older ignored, duplicate delivery, wrong readings and mappings refused, cross-tenant and package denial).

### Differences and follow-ups
- The spec does not say how many readings a delivery holds or how long they are kept. Defaults: one reading per delivery, kept 400 days (a year plus a month for year on year views). Batches of readings can come later as a separate target.
- No consumer reads the feed yet; dashboards in spec 021 are the expected first one.

## HR integration, connect and match (spec 019, slice 1: T001 to T005; US1, FR-002 to FR-004, FR-014 to FR-016, FR-018)

### Built
- Two packs under the capability `hr.integration`: `hr_rest` (a generic REST HR system: address and sign-in as custom REST, the employees path, where the records are, paging by page number or next link, and a JSON path per canonical field) and `hr_sftp` (the newest full CSV export on an SFTP server, with the clock data SFTP checks and a pinned host key, and a header name or column number per canonical field). Both map the HR system's status words to `active` and `terminated`, read ISO dates or the connection's date format, and run one read action `sync_employees` that may take up to 5 minutes. A next page link is followed only on the same host over https. A tenant has one HR connection (`conflict.hr.one_connection`).
- The canonical record is `HrEmployee` in `Integrations.Contracts`; values are trimmed and length checked, and a record with no ID, an unmapped status, a bad date or email, control characters, a duplicate ID, itself as manager or a manager chain that loops back is refused with a plain reason and listed on the run. The rest of the read goes on.
- `HrSync` stores each read in `integrations.hr_run` and `integrations.hr_employee` (migration 0014, forced RLS) and matches records to users through `IUserDirectory`: by work email then employee number by default (the order and either rule alone are tenant settings, `integrations.hr_settings`, changed with If-Match). A record that two users could match, or a user two records match, is a conflict and not matched. Links made by hand are kept by every later sync; a record unlinked by hand stays unlinked. Records not matched for over 30 days are shown as stale.
- The change threshold (FR-018): a run that would terminate or move more than the threshold percent (default 10) of the active matched employees is held before anything is applied. An HR administrator confirms or discards it; a newer run supersedes a held one. Holds, confirmations, settings changes, links and unlinks are audited.
- Each applied run publishes `HrSyncCompleted`. People (spec 010) handles it in `HrProfiles`: every profile field the connection maps (job title, office, hire date, employee number, and the new built-in fields grade, cost centre and employment type) takes HR as its source, audited as `people.field_source_changed` by `hr_sync`, and the values are stored in the new `hr_values` column (People migration 0003). HR fields are read-only and labelled from HR. People no longer matched lose their HR values. Only field names go into `ProfileUpdated`.
- Nightly sync at the connection's local time, a sync now, and a daily purge (runs after 90 days, leavers 30 days after their termination date) run in the connector job tick.
- Pages: HR REST and SFTP add forms in Connections and the same settings on the connection page; the admin section "HR integration" (`/admin/hr-integration`, permission `hr.integration_manage`, granted to tenant administrators and HR officers) with the connection, counts, last run, the held run to apply or discard, records by state with link and unlink, the matching rule and threshold, and the run history with refused records.
- Tests: `tests/Integration/HrSyncTests.cs` (matching, conflicts, refused records, cross-tenant emails never matched, read-only HR fields, manual links and unlinks kept, threshold hold, supersede, discard and confirm, If-Match, SFTP with a date format, settings rules, one connection, employee and package denial, loops and duplicate IDs). Acceptance: `hr-integration.spec.ts` changes the matching settings and finds both add forms, in English and Arabic with axe.

### Differences and follow-ups
- Where the spec is silent, the defaults are: one HR connection per tenant; a record missing from a full export is not treated as a termination (only the HR status or termination date ends employment); the threshold base is the active matched employees before the run, held when strictly over; a manual unlink stays unlinked until linked again.
- HR names do not feed profile names yet; directory sync stays the source of names (spec 003).
- The HR job can run up to 5 minutes inside the connector worker tick, which delays other jobs of that tenant meanwhile. A separate long job lane is a follow-up.
- `hr.mapping_changed` from section 13 is covered by `integration.connection_changed`, which already audits every settings change of the connection.
- Lifecycle (US2 to US4) needs leave and journeys from spec 022 R2. Slice 2 builds the 019 events and the consumers in 002, 003 and 010; the journeys of spec 022 consume them (see the spec 022 section).

## HR lifecycle: joiners, movers and leavers (spec 019, slice 2: T006 to T008; US2 to US4, FR-005 to FR-008)

### Built
- Each applied run is compared with what TeamBeats last applied, record by record (`HrLifecycle.Plan`, a pure function). A record first seen after the baseline is a hire. A changed org unit code, manager, job title or location is one assignment change: the four fields change together. A terminated status, or a termination date not older than the latest hire, is a termination. A former employee whose access ended and who is active again is a rehire. The new canonical field `assignment_date` gives the date the assignment takes effect. Until the change applies, TeamBeats keeps the assignment it had, so profiles do not run ahead of the move.
- Changes are stored in `integrations.hr_change` (migration 0015, forced RLS). Each change carries its kind, effective date, due time, from and to values, status, and the run and record it came from (FR-018). Due times use the tenant default calendar and its time zone.
  - A joiner is due the onboarding lead time before the start date. The default is 5 working days, and the tenant can set 0 to 30.
  - A termination is due at the start of the day after the last working day. When that day has already passed, or HR gives no date, it is due at once.
  - Assignments and rehires are due on their date.
- The connector tick applies due changes every 10 seconds, so an immediate termination takes effect within a minute, well inside the 15 minutes of FR-008. Every applied change is audited as `hr.change_applied` with its kind, effective date, run and record.
- A later run cancels an open change HR no longer reports: replaced, reverted, rescinded, or cancelled in HR. A hire cancelled before it is due is dropped and never becomes a termination.
- Events in `Integrations.Contracts`:
  - `EmployeeHired`, `EmployeeTransferred`, `EmployeeManagerChanged` and `EmployeeRehired` are published when the change applies.
  - `EmployeeTerminated` is published as soon as HR reports the termination, so offboarding can start (US4 scenario 1).
  - `EmployeeLeft` is published when the last working day has ended.
- Org codes: every code HR uses is listed in `integrations.hr_org_code` until an HR administrator maps it to a unit. An assignment to an unmapped code waits with the status `unmapped`, and nothing else in the run waits for it. Mapping the code applies the waiting assignments at once, and the mapping is audited as `hr.org_code_mapped`. Units are never created from HR codes: a new code is a proposal that waits for a mapping (US3 scenario 4).
- Consumers:
  - Organization (002) moves the main membership to the mapped unit, marked with the owner `hr`. It ends the previous main membership when TeamBeats or HR made it. A membership that directory mapping owns is kept as a secondary.
  - Profiles (010) set the reporting line to HR's manager, with HR as its source, and directory sync leaves such lines alone. A change that would create a loop is refused and reported.
  - Identity (003) disables the account when the person leaves (`identity.disabled_by_hr`), revokes their sessions and publishes `UserDeprovisioned` with the reason `hr`. The tenant's last administrator is kept enabled. Identity records which accounts HR closed (`identity.hr_leaver`, migration 0010). A rehire reopens only those accounts, only when the tenant allows it, and never one the directory has disabled.
- Pages: the HR integration page lists the org codes, waiting codes first, with a unit picker. It also shows joiners, movers and leavers by state (waiting, applied, cancelled) with their effective dates. Two new settings sit next to matching: the joiner lead time and whether rehires reopen accounts.
- Tests:
  - `tests/Unit/HrLifecyclePlanTests.cs` covers the planner: the baseline, future assignments, immediate and end-of-day terminations, rescinded and moved dates, joiners and cancelled hires, and rehires.
  - `HrSyncTests.Movers_wait_for_their_org_code_...` checks that a move waits for its code and then applies membership, manager and title together with its run. It also checks that a past last day disables at once and revokes sessions, that a notice period waits until due, that a rehire reopens the account only with the setting on, and that a joiner is prepared ahead and a cancelled hire is dropped. It also covers mapping to another tenant's unit, employee denial and package denial.

### Differences and follow-ups
- Where the spec is silent, these defaults apply:
  - The first applied run is the baseline. It plans only notice periods (termination dates today or later), never historic leavers or existing employees as joiners.
  - A record seen for the first time with a past termination date is history, not a leaver.
  - Moves and rehires without a date take effect on the day of the run.
  - Rehires do not reopen accounts unless the tenant turns that on.
- "HR may create org units" (FR-007, off by default) is not offered yet. Turning it on, with approval, is a follow-up.
- Secondary assignments from HR (edge case "employee in two org units") are not read yet. The canonical record has one org unit code.
- The onboarding and offboarding journeys of spec 022 consume these events, and the HR leave balance source of spec 019 US7 is built with the spec 022 leave ledger (see the spec 022 section).
- Open approvals of a mover are reassigned by spec 016 delegation rules once 016 consumes `EmployeeTransferred` (US3 scenario 2). That consumer is a follow-up of spec 016.

## HR values in service forms (spec 019, slice 3a: T009 and T010; US5, FR-009)

### Built
- Service forms (spec 016) get seven HR prefill sources: `hr.employee_number`, `hr.job_title`, `hr.grade`, `hr.cost_centre`, `hr.employment_type`, `hr.location` and `hr.hire_date`. The hire date fills a date field. The others fill text fields. They need the `hr.form_prefill` capability, checked through the entitlement service. Without it, the fields start empty.
- The form owner can mark an HR field as read-only. A read-only value always comes from the HR record on the server, whatever the browser sends (scenario 2). Only HR fields can be read-only or need a current value, and the form builder refuses anything else (`validation.services.field_prefill_hr_only`).
- Grade and cost centre are Confidential (section 8). They fill a field only when the service is classified Confidential or Restricted (scenario 4). The check runs every time a form is prefilled or submitted, so changing a service's classification later is covered too.
- The owner can mark an HR field as "must be current". When HR has not confirmed the stored copy for more than 7 days, the value is read from the HR system at that moment (scenario 3). The read goes through the REST pack's new optional "one employee" path (`employeePath` with `{record_id}`, and `employeeRecordPath`), on the HR system's own host only. A live read does not change the stored copy, because only full syncs feed lifecycle planning. When the read fails, or the connection has no such path (as with SFTP), the field stays empty and the prefill lists it as unconfirmed. The form then explains this. A read-only unconfirmed field blocks submit with `validation.services.prefill_unconfirmed`, so no value made up in the browser gets through.
- `IHrRecords` gains `CurrentAsync` and `HrRecordView.SeenAt` (the last time HR confirmed the record). `HrRecords` now implements the contract on top of `HrSync` and the REST pack.
- Tests:
  - `HrSyncTests.Forms_fill_the_requesters_hr_fields_...` covers the four scenarios, a stale copy that is read live, HR being down, each person getting only their own record, and a Workplace tenant getting nothing.
  - `FormValidatorTests` checks the new source and flag rules.

### Differences and follow-ups
- Where the spec is silent, these defaults apply:
  - HR fields fill only the person's own form. When someone files on behalf of another person, HR fields start empty, because HR values of others are for HR staff in the HR system.
  - A Confidential HR source in a form with a lower classification is allowed in the builder but never filled. The builder shows a hint.
- The live read uses the REST pack's normal HTTP time limit. A shorter limit for form opening can be added if HR systems turn out slow.

## My payslips (spec 019, slice 3b: T011 and T012; US6, FR-010 to FR-012)

### Built
- My payslips (`/payslips`, in the account menu) lists the signed-in person's payslips by period, newest first, with gross pay, deductions and net pay in the locale's number and currency format, and a PDF download.
- Payslips are read live from payroll through the HR REST pack each time. TeamBeats keeps no copy (section 8). The pack has new optional settings: the payslips path of one employee (`{record_id}`), where each value is in a payslip (period, gross, deductions, net, currency), and the PDF path of one period (`{record_id}` and `{period}`, year and month). Both paths stay on the HR system's own host over https. A PDF is at most 10 MB and must start as a PDF.
- Only the person's own payslips (scenario 2): the endpoints `GET /api/v1/me/payslips` and `GET /api/v1/me/payslips/{period}/pdf` take no person, so no role, manager or administrator can read someone else's. They need the `hr.payslips` capability and step-up (spec 026). Both answer with `Cache-Control: no-store, private`.
- Downloads (scenario 3) are audited as `hr.payslip_downloaded` with the period, and each list view as `hr.payslip_viewed` with the periods shown. Amounts are never in the audit. Every page of the PDF gets a stamp with the person's email and the time in UTC: a small line at the bottom and a light diagonal line. The stamp is a new content stream in a standard PDF font. Payroll's own content is left as it is (section 11). A PDF that cannot be stamped, for example one with a password, is not handed out.
- When payroll does not answer (scenario 4), the page says so and shows nothing.
- Payslips are never a search source, an agent tool, a connector action or an event, so they never reach search (012), models (013) or notifications (scenario 5).
- `GET /api/v1/me/payslips-available` tells the account menu whether to offer My payslips. It shows nothing about the payslips and needs no step-up.
- Dependency: PDFsharp 6.2.4 (MIT) stamps the PDF, because the base library cannot change PDF files. Only its low-level object model is used, with no font engine. Its `System.Security.Cryptography.Pkcs` dependency is raised to the .NET 10 release, because 8.0.1 has known advisories.
- Tests: `HrSyncTests.People_read_only_their_own_payslips_...` covers the list, the order, no-store, both audits, the watermark text, unknown and invalid periods, a manager and an administrator getting only their own, payroll down, step-up on both endpoints and a Workplace tenant refused. The e2e suite opens My payslips in English and Arabic with axe.

### Differences and follow-ups
- Where the spec is silent, these defaults apply:
  - There is no 10 minute per-user cache yet (section 7 default). Every view reads payroll, which is the safest reading of "never stale", and a cache can be added if payroll load needs it.
  - The SFTP pack drops files and cannot read payslips, so My payslips says payroll is not available for it.
  - The watermark uses the email, since the standard PDF fonts cannot draw Arabic names.
- Kiosk mode (spec 030) is not built yet. When it is, it must hide My payslips (scenario 3).
- My payslips is in the account menu. Listing it as a service in the catalogue (section 10) is a follow-up of spec 016.
- "Your payslip for November is available" notifications need a payroll signal and are not built.

## HR integration final checks (spec 019, T014; section 15)

### Built
- `HrSyncTests.A_full_sync_of_twenty_thousand_employees_finishes_well_within_thirty_minutes` reads 20,000 employees from the HR REST pack in pages of 1,000, matches and stores them, applies the due lifecycle changes and gives every profile its HR values. It then runs a second full sync with a normal month of changes under the threshold: 1,000 new titles applied on the day, 500 moves waiting for an unmapped code and 100 leavers waiting for their last day. Each run must finish under 10 minutes, a third of the budget; the whole test, seeding included, takes about 30 seconds locally.
- `HrSyncTests.The_payslip_list_loads_p95_under_two_seconds_when_payroll_takes_close_to_a_second` holds payroll at 900 ms on every read and checks the list p95 under 2 seconds. Every view reads payroll, so this is the worst case.
- `HrSyncTests.Unmatched_records_are_retried_then_listed_as_stale_and_hr_fields_show_only_where_profile_visibility_allows` covers FR-015 (a record unmatched for over 30 days is listed as stale, is still retried and matches once the account exists) and FR-017 (grade, cost centre and employee number show to HR officers in scope only).
- Evidence: `specs/019-hr-integration/evidence.md`. No API changed, so the OpenAPI document is unchanged.

### Differences and follow-ups
- Due lifecycle changes are applied 500 per connector tick (every 10 seconds), so a run with thousands of due changes takes a few ticks to apply them all. The test runs the ticks it needs.
- SC-001 (joiners ready on day one in pilot tenants) needs a pilot tenant.
- Still open from the slices: creating units from HR, secondary assignments, HR names on profiles, a separate long job lane, the payslip cache, kiosk mode, My payslips in the catalogue and payslip notifications.

## HR workflows (spec 022, R2 slices 1 to 6 and T024)

The plan (`specs/022-hr-workflows/plan.md`, sections "R2 slices" to "Slice 6") holds the data model, API, audit and the smallest safe default of every slice. This section lists what was built and what is left.

### Built
- Slice 1, leave (T009 to T014): leave types and policies, a ledger kept by TeamBeats with monthly or yearly accrual and carry-over, requests with the live balance, day count and team overlap, replacement asks, approval on the process engine with tasks in the inbox, cancel and shorten, the team availability view, HR adjustments and the Leave module service.
- Slice 2 (T015 to T017): balances read from HR when HR owns them, approved leave handed to HR with a retry queue, recount of approved leave when holidays change, and reduced hours periods such as Ramadan.
- Slice 3, journeys (T018, T019): onboarding, offboarding, transfer and promotion templates with steps relative to an anchor date, started by HR and directory events or an approved resignation, moved with the anchor, attestation steps (spec 025), the access removal checklist with escalation, exit interviews, the journeys dashboard and pre-boarding behind a tenant setting.
- Slice 4, attendance (T020): web sign in with an optional location check, clock data from spec 017 imports, working hours rules, exceptions, corrections, compensation days and the monthly summary with export.
- Slice 5, letters and data change (T021, T022): bilingual letter templates issued as PDF with a reference and a public verification page; employee data changes with documents, written back to HR through spec 019 and kept nowhere afterwards.
- Slice 6, expenses (T023): the expense policy and rates kept by a new `finance_officer` role, claims checked against the window and the grade limit, converted at finance's rate, flagged as duplicates, routed by amount and handed to the finance system through spec 017; AI receipt reading through the gateway as a Confidential attachment, as a suggestion only.
- T024:
  - Profiles show "out of office" for approved leave that covers today (FR-005). `ILeave.AwayAsync` gives the first and last day of the leave run around today, joined over weekends and holidays, and never the type. The People profile reads it; a pending request does not show.
  - The journey worker now runs more batches of due steps in the same tick while a tenant's batch of 500 comes back full, within a 90 second budget and one batch per tenant before any tenant gets a second. Before, a day with more than 500 due steps took one two minute tick per 500.
- Performance (section 15):
  - `HrWorkflowPerformanceTests.The_leave_request_page_with_balance_and_overlap_loads_p95_under_one_second`: the three calls of the page, with 40 colleagues away in the same week. About 80 ms p95 locally.
  - `HrWorkflowPerformanceTests.Journey_steps_due_on_a_busy_day_all_open_in_one_tick_well_within_five_minutes`: 1,500 steps due at once open in one tick, in about 9 seconds locally. With the two minute tick, a step opens within about 2.2 minutes of its date.
  - `ClockImportTests.Fifty_thousand_punches_import_within_the_budget`: 50,000 punches from an uploaded file to the attendance punches, within one minute for the file and five minutes in total.

### Differences and follow-ups
| Item | Status |
|---|---|
| Eligibility by gender or religion (FR-001) | Only membership type rules are built; gender and religion rules wait for a spec decision on which attributes may be used |
| Delegation for the leave period (FR-004) | Leave approvals follow spec 016 delegation, but the leave page does not offer to set one up for the period yet |
| Learning assignments in onboarding (FR-015) | Wait for spec 028; a template step can be a manual task until then |
| Ownership hand-overs at offboarding (FR-017) | The access removal checklist and its escalation are built. Specs 009 and 011 handle a leaver on their own side (an ownerless site is flagged; an approver who left falls back on the route). Cancelling a leaver's open requests (spec 016 edge case "requester leaves") is not built yet |
| Expense policy page | The policy is edited as validated JSON on `/admin/expenses`; a form for categories and tiers is a follow-up |
| Rate source | Finance keeps the rate table by hand; a rate feed through spec 017 can fill the same table later |
| Receipt reading use case | Spec 013 has no receipt use case, so it uses `summarize`. Under the default AI policy it is blocked, because receipts are Confidential |
| SC-001 to SC-004 | SC-001 and SC-004 are covered by the e2e suites (`leave.spec.ts`, `letters.spec.ts`), which do the same steps without timing a person. SC-002 and SC-003 need a pilot tenant and are not proven |

## Communities, posts and comments (spec 023, slice 1: T001 to T004; US1, FR-001 to FR-004, FR-023)

### Built
- The `Engagement` module (ADR-0013) with schema `engagement` and forced RLS on every table: communities, members, channels, posts, hashtags, mentions, comments, reactions and follows. Later slices add their own tables (reports, mutes, Speak Up, polls, surveys, recognition, celebrations, albums) in their own migrations, so T001 covers the module and the slice 1 tables.
- Five permissions (`engagement.participate`, `engagement.moderate`, `engagement.manage`, `engagement.survey_publish`, `engagement.recognition_approve`), the new built-in role `engagement_administrator`, the six capability keys of section 9 and the audit actions for community created, changed and archived, member changed and post pinned.
- Communities: open, approval required and closed. Closed communities answer "not found" to anyone who is not a member or a moderator in scope, in the directory, the page, the feed and search. An approval community lists its name to everyone but shows posts only to members; requests go to the owners. The last owner cannot leave or be demoted.
- Department communities: a community linked to an org unit takes its members from the unit and the units below it, read live from the organization contract (spec 002), so a move changes membership at once.
- Posts in the shared body format (spec 009 editor), comments with one reply level, five reactions, mentions, hashtags, pinning, following and editing. A mention tells only people who can read the community (spec 015); the others come back to the author as "not told". Followers hear about new comments at most once an hour per post, folded into one grouped notice.
- Posts are indexed for search (spec 012) as the `discussion` kind, with the community's access rule checked at query time.
- Pages: the communities directory with the create form for engagement administrators, the community page with channels, composer and feed, the members page, the post page and the hashtag page. In English and Arabic.
- `GET /api/v1/engagement/rights` tells the directory whether to offer the create form; the API still checks every create.
- The shared `Card` component now keeps footer actions above the covering heading link, so a linked card can carry a button.

### Smallest safe defaults
| Gap | Default |
|---|---|
| Who sees an approval community | Everyone sees its name and description; posts only members and moderators in scope |
| Moderators of closed communities | A person with `engagement.moderate` on the community's scope can see and read it |
| Department membership and child units | People in units below the linked unit are members too |
| Follower notices | At most one notice per post per hour, folded into the 15 minute group of spec 015 |
| Search and the assistant | Discussions are internal content and are open to assistant retrieval under the same access rule |
| Communities capability | On in every package (section 9), so there is no entitlement denial test for it; the other keys are tested in their slices |

### Differences and follow-ups
| Item | Status |
|---|---|
| Department membership (plan, key decisions) | The plan stores unit members in `community_member` from 002 events; slice 1 reads them live from the organization contract instead, which needs no sync and cannot drift |
| Images and files in posts (FR-003) | Wait for the galleries slice (T014), which brings scanning through spec 026 |
| Moderator removal of comments | Done in slice 2 (T005, T006) |
| Home widgets (FR-022) | T015 |

## Moderation (spec 023, slice 2: T005 and T006; US8, FR-018 to FR-021)

### Built
- Reports on posts and comments with a reason and an optional note. Only someone who takes part in the community may report, never their own item, and a second report from the same person changes nothing. At three open reports from different people the item is hidden and audited, and a hidden post leaves search.
- The tenant keyword filter: versioned configuration (constitution XXIX) that engagement administrators change with If-Match. A word or phrase matches whole and ignoring case. A post or comment that matches is held: nobody but the author and moderators sees it, and mentions and search wait until a moderator approves it.
- The moderation queue at `/communities/moderation`: held, hidden and reported items in the communities the viewer moderates, with report counts by reason and the reporters' notes but never who reported. Approve (or keep, for an item still shown) publishes it and closes its reports; remove needs a reason the author reads on the item and is told about.
- Mutes per community: a moderator mutes a member for 1 to 365 days with a reason; the member still reads and reacts but cannot post or comment, and the community page says until when. Muting and unmuting are audited and the member is told.
- `GET /api/v1/engagement/rights` now also says whether to offer the queue (`canModerate`) and the keyword filter (`canManageKeywords`). The queue link also shows on each community a person moderates.
- The test world's seeded people now renew their sign-in time before a change once it is five minutes old. Seeded sessions used to age past the ten minute step-up window once the suite ran longer than ten minutes, so step-up actions late in the run failed with 401.

### Smallest safe defaults
| Gap | Default |
|---|---|
| Keyword holds on comments and edits | They apply to comments and to edits of a post too |
| Telling moderators about new items | No notice; the queue is the place to look |
| Showing reporters | Never shown to moderators or the author |
| Who can be muted, and for how long | Not oneself and not an owner; at most 365 days; the web offers 1, 7, 30 or 90 days |
| Removal notice | Says the item was removed; the author reads the reason on the item |
| Answer to a reporter | The same however many people reported it |
| Comments of a removed post | Leave view with the post |
| Deleting a removed post | The author cannot, so the moderator's decision stays on record |

### Differences and follow-ups
| Item | Status |
|---|---|
| Moderators scoped to an org unit | Reach the queue from their community's page; the directory link covers community roles and tenant wide rights |
| Notices to moderators about new items | Not in the spec; can come with a later notification setting |

## Speak Up (spec 023, slice 3: T008 and the Speak Up half of T007; US2, FR-005 to FR-007, FR-025)

### Built
- Channels have a kind: discussion or Speak Up. Every post in a Speak Up channel waits in the review queue (held reason `speak_up`), and so does an edit to one. A Speak Up channel needs the `engagement.speak_up` capability, which is off in Workplace.
- Anonymous posting in a Speak Up channel that allows it. Only engagement administrators can allow anonymous posts in a channel; a community owner alone cannot. The page tells the person plainly, before they post, that their name is not kept.
- An anonymous post is written by its own use case to `engagement.anon_post`. That table has no user column and no time of day, and its random id carries no time. Nothing is audited, followed or sent as an event when it is posted. The person is checked only to see that they may post and are within the limit.
- The daily limit (5 anonymous posts a person a day) counts by an HMAC of the user id under a random key for the day. Keys and counts from earlier days are deleted on the next post, so an old hash cannot be matched to anyone.
- In the moderation queue an anonymous post has no author and shows only its day. A moderator publishes it, optionally after changing the words (the post then shows "edited by a moderator"); answers it privately; or declines it with a reason. The published post has no author. After a decision the queue row keeps only the outcome.
- The author gets a private link once, when they post. The token sits after `#`, so it never reaches the server in an address, and the reply page posts it in the request body. The link works while the post waits. It stops working once a decision has been shown.
- The web message check now reads camelCase namespaces such as `engagement.speakUp`; before this, files with such a namespace were not checked.

### Smallest safe defaults
| Gap | Default |
|---|---|
| Who turns anonymity on | Engagement administrators, per Speak Up channel |
| Anonymous daily limit | 5 a person a day |
| "One-time reply link" | Shown once at posting; readable while pending; stops after a decision has been shown |
| What "answers it" means | A private answer to the author through the link; nothing is published |
| Moderator edits | Only for anonymous posts before publishing; named Speak Up authors edit their own posts, which go back to review |
| Comments on published anonymous posts | Allowed and named, as anywhere else |
| Words kept after a decision | The queue row drops the words; a published post keeps them on the post |

### Differences and follow-ups
| Item | Status |
|---|---|
| Analytics counts for anonymous items (FR-024) | Come with T015 with the other engagement counts |
| Session last-seen times | The session table records when a person was last active, which is not tied to any post; noted in case a stricter reading of FR-005 is wanted |

## Polls (spec 023, slice 4: the poll part of T009 and T007; US3 scenario 1, FR-008)

### Built
- Polls belong to a community and come with communities, so Workplace has them too (as the capability migration recorded). Anyone who can post in the community starts one. Everyone who reads it and takes part votes.
- Single or multiple choice, an end time within a year, and results shown either after a person votes or only after close. The author and the community's moderators always see results, and in a named poll they also see who chose what.
- One vote per person. Where the poll allows it, a vote can be changed until close. In an anonymous poll votes go into counts that have no user column. Only the fact that a person voted is kept, in a separate table that is deleted when the poll closes. Anonymous votes cannot be changed.
- After the first vote, options can only be added (edge case). The author adds them while the poll is open, up to 20.
- A poll closes at its end time the first time anyone opens it afterwards, or earlier when its author or a moderator closes it. Closing sends `PollClosed`.
- Each community has a Polls page, linked from the community page. In English and Arabic.

### Smallest safe defaults
| Gap | Default |
|---|---|
| Where polls live | In a community, open to its readers; surveys carry audience targeting |
| Who starts a poll | Anyone who can post in the community |
| Who sees results early and who voted | The author and the community's moderators; voters only in named polls |
| Changing an anonymous vote | Not allowed, since nothing links the vote to the person |
| Muted members | Still vote, as they still react |
| Closing at the end time | Done when the poll is next opened; no background job |

### Differences and follow-ups
| Item | Status |
|---|---|
| Active poll widget for home (008) | T015 |
| Points for poll votes | T011 |

## Surveys (spec 023, slice 5: the rest of T009 and T007; US3 scenarios 2 to 5, FR-005, FR-009, FR-010)

### Built
- Surveys need `engagement.surveys`, so Workplace runs polls without surveys. A person with `engagement.survey_publish` on an org unit builds a survey for that unit and every unit below it, so a survey for Acme Gulf leaves Acme Africa out. Survey authors find the page from the communities directory.
- The builder takes a title, an introduction, anonymous or named, a minimum number of responses, an end date and up to 50 questions: one choice, several choices, a rating from 1 to 5, or a written answer, each optional or required. A draft changes as a whole until it is published; after that the questions and audience are fixed.
- People in the audience answer once. An anonymous response has a random id, no user and no time, only the group it counts in. "Has responded" is kept in its own table and deleted at close, so nothing is left that says who answered. Nothing about a response goes to the audit trail.
- Groups are the units directly below the survey's unit (or the unit itself for people placed there). In an anonymous survey nothing shows before close, nothing shows when the survey is under the threshold, and any group under it is hidden in the results, the group filter and the export. When the hidden groups could still be worked out from the total (only one hidden, or together under the threshold), the next smallest group is hidden too.
- The author and publishers on the unit see results and export them as CSV: the whole survey, then each group that shows, with written answers listed in alphabetical order without names. A named survey also shows them who answered what.
- Engagement administrators set the tenant threshold (default 5, range 3 to 50) with If-Match; a survey may raise it up to 50 but never go below it.
- Publishing and closing are audited (`engagement.survey_published`, `engagement.survey_closed`), as are threshold changes. A survey closes at its end date the first time anyone opens it, or earlier by its author or a publisher, and sends `SurveyClosed`.

### Smallest safe defaults
| Gap | Default |
|---|---|
| Audience | One org unit and everything below it; richer audiences (spec 009 clauses) can come later |
| Groups for the threshold | The units directly below the survey's unit |
| When anonymous results show | Only after close, so nobody can watch a single answer arrive |
| Working a hidden group out from the others | The next smallest group is hidden as well (complementary suppression) |
| Threshold for named surveys | Not applied; managers already see names |
| Who sees results | The author and people with survey publishing on the survey's unit |
| Changing an answer | Not allowed |
| Order of written answers | Alphabetical, so the order says nothing about who or when |

### Differences and follow-ups
| Item | Status |
|---|---|
| Reminders to people who have not answered | Not built; the "has responded" table is ready for it |
| Sharing results with managers (spec open question) | Not built; results stay with the author and publishers |
| Notifying the audience on publish | Not built; the survey shows under "To answer" |
| Survey widget for home (008) | T015 |


## Recognition and points (spec 023, slice 6: T010 and T011; US4, FR-011 to FR-013)

### Built
- Anyone who takes part thanks up to 20 people, or one team (an org unit), with a message and optionally a company value and a badge. Nobody can recognize themselves. Recognition is in every package (`engagement.recognition`).
- Engagement administrators keep the values and badges. Each badge has an icon from the design system set and can be limited to givers in one org unit and below; the give form offers only the badges the giver may give, and the API refuses others. Entries left out are retired, never deleted, so old shout-outs keep their value and badge.
- Shout-outs show on the recognition wall, newest first, and on the person's profile (spec 010) unless they turn that off. A hidden list looks the same as an empty one. The people thanked and their managers (from the reporting line) are told, under a new notification category, `recognition`, that people can mute. `RecognitionGiven` is published.
- The giver or an engagement administrator removes a shout-out; its points go with it.
- Points need `engagement.points` (Workplace Plus and above) and the tenant setting. Defaults from the spec: 10 for a shout-out received, 2 for a post, 1 for a poll vote, at most 50 a day per person, and only the giver's first 5 shout-outs each day earn points. Points for posts come from the `PostPublished` event. Settings are versioned and changed with If-Match, and every change is audited (`engagement.points_config_changed`).
- When points are off, or not in the package, the points and leaderboard endpoints answer not found, the page shows no counts, and nothing more is recorded.
- Leaderboards are off until an administrator turns them on. They cover this week (Sunday to Saturday), this month or this quarter in the tenant's time zone, for the whole tenant or one org unit and below, and leave out people who opted out and people who left.

### Smallest safe defaults
| Gap | Default |
|---|---|
| Team shout-outs and points | A team shout-out earns nobody points |
| Points for anonymous posts and anonymous poll votes | None, since a points entry would name the person |
| Points for a held post | Earned when it is published, through `PostPublished` |
| Changing a poll vote | Earns nothing more; one entry per poll |
| Who removes a shout-out | The giver and engagement administrators |
| Leaderboard size | Top 20 |
| Notification category | New `recognition` category, not mandatory, no email by default |

### Differences and follow-ups
| Item | Status |
|---|---|
| Flagging mutual recognition loops to administrators (spec section on gaming) | Not built yet; the daily caps and no self recognition are in place |
| Recognition and leaderboard widgets for home (008) | T015 |
| Shout-outs in search | Not built |

## Team and star of the period (spec 023, slice 7: T012; US5, FR-014)

### Built
- An engagement administrator opens an award period for a person or a team, for the whole tenant or an org unit and everything below it, with the last day for nominations (from today to a year ahead). Opening is audited (`engagement.award_opened`). Awards use the `engagement.recognition` capability.
- Anyone who takes part nominates one person or one team in the period's scope, with a reason. Nobody nominates themselves, people who left cannot be nominated, and one nomination per person is kept: sending again replaces it. Nominations close after the last day in the tenant's time zone.
- Recognition approvers with `engagement.recognition_approve` on the period's unit (or on the tenant for a tenant-wide period) see the nominations, grouped by nominee with every reason and who gave it, and pick one to five winners from the nominees, also after nominations close. That ends the period and is audited (`engagement.award_decided`).
- Winning people are told under the `recognition` category. Everyone sees the winners on the awards page.
- An announcement draft (type `announcement`, default site) starts in content (009) through the new `IAnnouncementDrafts` contract, in every enabled locale, with the award title and the winners as a list. Communicators finish and publish it under the normal spec 009 rules; approvers and engagement administrators get a link to it.

### Smallest safe defaults
| Gap | Default |
|---|---|
| Who may nominate | Anyone who takes part; only the nominee must be in scope |
| Nominations per person | One per period, replaced when sent again |
| Number of winners | 1 to 5, chosen from the nominees |
| Choosing before nominations close | Allowed; choosing ends the period |
| Who sees nominations | Approvers in scope only; others see their own |
| Announcement author check | The draft is a hand-off: it skips the author check because only someone with publish rights can make it visible |
| No content in the package | No draft; the winners still stand |
| Team winners | Not notified one by one; the announcement covers them |

### Differences and follow-ups
| Item | Status |
|---|---|
| Reminders before nominations close | Not built |
| Awards on home (008) | T015 |

## Celebrations (spec 023, slice 8: T013; US6, FR-015, FR-016)

### Built
- People who want to share their birthday give a month and a day on the celebrations page. No year is asked for or stored, and choosing not to share deletes both values. Nobody else's birthday is stored or shown.
- Work anniversaries come from the hire date in people profiles (010) and new joiners follow the people newcomer rules of FR-009, including the person's opt-out. Engagement reads both through the new `ICelebrationDates` contract and never queries people tables.
- The page lists today and the next six days in the tenant's time zone. By default it covers the people in the viewer's org units and the units below them, and the viewer can switch to the whole organization. A viewer with no unit sees everyone.
- On the day, colleagues send a greeting. The person gets one notification (category `recognition`) per colleague and occasion, however often the button is pressed. Nobody greets themselves, and a greeting for someone who is not celebrated that day answers not found.
- Engagement administrators switch birthdays, anniversaries and joiners on or off. Settings are versioned, changed with If-Match and audited (`engagement.celebration_settings_changed`).

### Smallest safe defaults
| Gap | Default |
|---|---|
| Birthdays for the tenant | On, but each person still has to opt in |
| Work anniversaries | Off until an administrator turns them on, because the hire date is a managers and HR field; each person can leave theirs out |
| New joiners | On, following the people newcomer window and opt-out |
| 29 February birthdays and hire dates | Celebrated on 28 February in other years |
| How far ahead | Today and the next six days |
| Greeting notification category | `recognition` |
| Capability | `engagement.communities` |

### Differences and follow-ups
| Item | Status |
|---|---|
| Celebrations widget for home (008) | T015, with the other engagement widgets |
| Birthdays from HR (019) | Not used; birthdays come only from the person's own opt-in, so no birth year ever reaches TeamBeats |

## Photo galleries (spec 023, slice 9: T014; US7, FR-017)

### Built
- Members who may post in a community make albums, and an album can ask a moderator to approve each photo. Everyone who can read the community opens its albums.
- Photos are PNG or JPEG up to 10 MB. Before anything is stored, the file is checked to be the image it claims to be, and location, camera and other metadata (EXIF, XMP, comments and text chunks) are removed. The tenant's malware scanner (spec 001) then checks the cleaned file, and only a clean result is stored, in tenant scoped object storage under `engagement/photos/`.
- The content safety check goes through the AI Gateway (use case Summarize, classification Internal) with a structured yes or no answer. A safe photo shows at once, unless the album needs approval. A photo the check flags, or one it could not check, waits for a moderator, and only the uploader and the moderators see it until then.
- Moderators approve or remove photos, and both are audited. The uploader can delete their own photo. Removing a photo deletes its file and tags.
- The uploader tags up to 20 people who can read the community. A tagged person is notified when the photo shows (category `mention`) and can remove their own tag.
- Galleries need the `engagement.galleries` capability.

### Smallest safe defaults
| Gap | Default |
|---|---|
| Who makes albums | Members who may post in the community |
| Scan result other than clean | Nothing is stored; an infected file is refused and an unavailable scanner asks to try later |
| Safety check flags the photo or is unavailable | Held for a moderator, never shown or refused on its own |
| Who can be tagged | People who can read the community, at most 20 per photo |
| Tag notification category | `mention` |
| File types and size | PNG and JPEG, 10 MB, at most 12000 pixels a side |

### Differences and follow-ups
| Item | Status |
|---|---|
| Resizing (US7 scenario 1) | Follow-up. There is no image library in the stack yet, so photos are stored at their cleaned original size; adding one needs the dependency review of hard rule 6 |
| EXIF orientation | Lost with the metadata; comes back with resizing |
| Videos and reporting a photo | Follow-ups |

## Home cards and performance (spec 023, slice 10: first part of T015; FR-022, spec 008 FR-055)

### Built
- Six components that any page, the home included, can place: latest discussions and the active poll of one community, recognition, the points leaderboard, celebrations and awards. Each is registered by Engagement for spec 032 with the capability behind it, so a tenant whose package lacks it is not offered the card.
- Every card reads the same endpoint as the full page it links to (FR-022). The discussions card is the community's first feed page and the poll card is the community's polls list, so a closed community shows nothing to someone who cannot read it. A card whose feature is off for the viewer shows as empty, never as an error.
- Performance (section "Non-functional"):
  - `EngagementPerformanceTests.A_community_feed_first_page_stays_under_800_ms_p95_at_10000_posts`: about 10 ms p95 locally.
  - `EngagementPerformanceTests.An_anonymous_survey_of_10000_respondents_aggregates_in_under_10_seconds`: four questions over ten groups, about 0.2 seconds locally.

### Smallest safe defaults
| Gap | Default |
|---|---|
| Which community the discussions and poll cards show | The one the page editor picks; there is no cross-community feed on home yet |
| Cards on every employee's default home | None added. The cards are offered in the page builder and in home personalization, so no tenant's home changes on its own |
| Leaderboard period on the card | This month |
| Picker category | `people` |

## Images and files in posts (spec 023, slice 11: FR-003; US1)

### Built
- A member uploads images and files to a community before posting (`POST /api/v1/engagement/communities/{id}/attachments`), then names the uploads on the post. A post takes at most 10 images and 5 files, and only the author's own uploads in the same community.
- Every upload is scanned for malware. Images lose their metadata the same way gallery photos do and pass the content safety check through the AI Gateway; the scan and the check are shared with galleries in `MediaChecks`.
- Files are PDF, Word, Excel, PowerPoint, plain text and CSV, and the bytes must match the declared type.
- `GET /api/v1/engagement/attachments/{id}/file` opens an attachment for whoever may read its post. A loose upload opens only for its uploader. Images show inline and documents always download, with `nosniff`.
- Editing a post without the attachments list keeps them; an empty list removes them and their stored objects.

### Smallest safe defaults
| Gap | Default |
|---|---|
| A flagged image, or one the safety check could not judge | Refused, not held for review; the person can try again later |
| Attachments on Speak Up posts | None, named or anonymous, so a file cannot reveal its author |
| Sizes | 10 MB per image, 25 MB per file |
| Uploads never used in a post | Removed after one day, on the uploader's next upload |

## Engagement counts for analytics (spec 023, slice 12: FR-024)

### Built
- Engagement emits `engagement.action` for named posts, comments, reactions, poll votes, recognition, survey responses and joins, with the community where there is one. These go through the outbox like other module events and carry the pseudonymous key and unit.
- Anonymous actions (an anonymous Speak Up post, a vote in an anonymous poll, a response to an anonymous survey) emit `engagement.anonymous_action`, a schema marked unkeyed. It is written at once in the request's transaction, never queued, because a queued message keeps the correlation and exact time. The row holds no person, unit, location or locale, and its times are cut to the day.
- Both events are server only: the web collector drops them.
- Tests: `EngagementAnalyticsTests`.

### Smallest safe defaults
| Gap | Default |
|---|---|
| Which actions count | The seven named and three anonymous actions above; views are already counted by the page events |
| Detail kept for anonymous counts | The action and the day only |
| Engagement panels on the spec 021 dashboard | A follow-up; the counts are stored and can be queried from now on |

## Courses, quizzes, paths and certificates (spec 028, slice 1: T001 to T003, API of T004 and T005; US1, US3, FR-001 to FR-004, FR-009, FR-010, FR-012, FR-018, FR-019)

### Built
- The `Learning` module (ADR-0013) with schema `learning` and forced RLS on every table: courses and their published versions, assignments, lesson progress, quiz attempts, extra attempts, certificates, paths and their courses.
- Permissions `learning.take`, `learning.manage`, `learning.team_view`, `learning.report` and `learning.assign_linked`, the learning administrator built-in role, and the capabilities of section 9 (`learning.scorm` Enterprise and Government only).
- Two new contracts so lessons reference material and never copy it: `IPublishedPages` in Content (a published page of any type the caller may read) and `IDocumentReferences` in Documents (the version in force, once scanned clean).
- The builder (`/api/v1/learning/manage/courses`): a draft definition of titles and summaries per language, page, document, video and quiz lessons, and quizzes of single choice, multiple choice and true or false questions. Publishing checks each source is published, readable and, for a video lesson, a video, keeps each source's version, and shows a later change of a source as "changed since publishing" until the next publish.
- The player (`/api/v1/learning/courses/{id}`): learners in the audience, with an assignment, or following a path that holds the course see the latest published version, never the right answers. Lessons finish only when the learner can open their material; quizzes are scored on the server within their attempts; the last lesson is kept for resuming; the last lesson done completes the course and issues a certificate. `CourseAssigned`, `CourseCompleted`, `CertificateIssued` and `CoursePublished` go through the outbox, and `learning.progress` goes to analytics as a server-only event.
- Certificates: an ID of twelve characters without look-alikes, verified by the holder and by people with `learning.report` covering one of the holder's units, inside the tenant only. The PDF is printed through the HTML route of the document converter when downloaded.
- Paths: ordered courses, published once every course in them is published. A path opens its courses to its audience even when a course is not open for enrolment on its own, and a course that needs the one before it stays locked until that one is completed.
- Tests: `LearningCourseTests` (5 tests), `LearningRulesTests` (unit).

### Smallest safe defaults
| Gap | Default |
|---|---|
| Which version of a page a learner reads | The page as published now; the course keeps the version it was published with only to show the builder what changed |
| Who verifies a certificate (FR-010 "authorized users") | The holder and people with `learning.report` covering one of the holder's org units or the tenant |
| When the certificate PDF is made | When it is downloaded, in the holder's language when the course has it; the record and its ID are kept at completion |
| Retraining on the first version | Ignored: nobody has completed a course before its first version |
| Self enrolment | Only in courses marked open whose audience includes the learner, or through a published path |
| Attempts after a pass | Refused; the score of each attempt is kept and the answers are not |
| Learner who cannot open a lesson's material | The lesson cannot be marked done; the builder is responsible for giving the audience access |
| Catalogue search through 012 | The catalogue filters by text in any language; courses are also in search since slice 2 |

## Learning pages, home card and courses in search (spec 028, slice 2: T004 and T005; US1, US3, FR-004, FR-009, FR-010, FR-018, FR-019)

### Built
- Pages under `/learning`: My learning (assigned, in progress and completed courses, and paths), the course catalogue with search, the course player (enrol, lesson outline, resume at the last lesson, page, document and video lessons opened where they live, quizzes with attempts left), paths with locked steps, certificates with the PDF download, and a page to check a certificate ID.
- Building under `/learning/manage`: course and path lists, the course builder (languages, titles and summaries, category, owning unit, audience by unit, open enrolment, lessons in order, quiz questions and answers, save, publish with or without retraining, retire) with a Learners tab that shows progress and quiz attempts and gives one more attempt, and the path builder (courses in order, prerequisites, audience, publish).
- The `learning.my` home card (spec 032 component, needs `learning.courses`): the viewer's open courses, nearest due date first.
- Courses in search (spec 012): a `learning` source with the `course` kind. A published course gives one unit per language with its summary, category and lesson titles; a person finds it only when its audience includes them or they already have it. Publishing, retiring and editing a published course reindex it.
- Tests: `LearningCourseTests` (6 tests, search added), `ExperienceValidationTests` (the card), `learning.spec.ts` (end to end in English and Arabic, with axe).

### Smallest safe defaults
| Gap | Default |
|---|---|
| How a builder picks a lesson's page or file | Paste its link or ID; the builder takes the ID from the link and the API checks it on publish. A picker can follow |
| How a video plays | It opens in the document library, which already previews and scans it; the player does not embed media |
| Where the learning pages appear in the menu | Tenants add `/learning` to their navigation; the menu shows it with the book icon |
| Who sees a course in search | The same people the catalogue shows it to, plus anyone who already has it |

## Assignment rules, reminders, overdue escalation and recurrence (spec 028, slice 3: T006 to T008; US2, US4, FR-005 to FR-008, FR-011, FR-012, FR-017)

### Built
- `AssignmentRules`: a rule gives a published course to an audience of units, named people, or everyone within the unit the administrator manages (`learning.manage` on that unit), with due days, an optional recurrence in months and a grace period. At most 50 rules per course. Creating and ending a rule are audited (`learning.assignment_created`, `learning.assignment_withdrawn`).
- Each person a rule covers gets their own due date, counted from the day they are covered. A person covered by several rules has one open assignment with the earliest date (FR-017). Leaving the unit or being disabled withdraws open assignments that only rules gave; completions stay in history.
- Membership changes (`OrganizationMembershipChanged`, `TenantMembershipDisabled`) re-apply the rules for that person at once. A maintenance job (`LearningMaintenanceWorker`) re-evaluates every active rule hourly, sends reminders, marks overdue assignments, notifies their manager and expires recurring completions into a new assignment.
- A retraining version (`CoursePublished` with retraining) gives the course again to everyone whose last completion is of an older version.
- `CourseOverdue` is published once per assignment. Completion sets the assignment's and the certificate's expiry from the shortest recurrence among the rules covering the learner.
- Builder: an Assignment rules tab on the course builder lists rules with who they cover and creates or ends them.
- Training other modules ask for, through `ILearningObligations` (Learning.Contracts): one obligation per person, course and source key, done when the course is completed, cancelled with its source. A cancelled source withdraws only the assignments nothing else asks for.
- Onboarding (022): a `training` journey step names 1 to 10 published courses. When it opens, the joiner is given them due with the step; `CourseCompleted` (and the journey worker) close the step once every course is completed. A joiner who already completed them skips the step; a cancelled journey cancels what is still owed.
- Policies (025): `PUT /api/v1/policies/{id}/training` links a published course (compliance officers with `policy.manage` and `learning.assign_linked`, audited as `policy.training_linked`). A material version in force gives the course to everyone the policy applies to; a completion from before that version does not count. With "attest only after the course", attesting is refused until it is completed, and the attest panel links to the course.
- Pages: an Assignment rules tab on the course builder; a Training step type and its courses in the journey template editor, with an Open My learning link on the journey; Linked training on the policy page; the training notice on the attest panel.
- Tests: `LearningAssignmentTests` (5 tests: newcomers and leavers, duplicates and scope, reminders and weekly escalation, recurrence and retraining, entitlement and retirement), `LearningObligationTests` (2 tests: the onboarding training step, and policy linked training with the attestation wait), `learning.spec.ts` (a rule from the builder).

### Smallest safe defaults
| Gap | Default |
|---|---|
| Which package has rules | `learning.mandatory`; without it the rule endpoints are refused and the job does nothing |
| What scope a rule has | Its own unit; the audience and the named people must sit inside it. A rule with no unit needs `learning.manage` for the whole tenant |
| A due date on a weekend or holiday | Moved to the next working day on the learner's calendar (020) |
| Notification category | Reminders and overdue notices use `attestation_due`; a new assignment uses `request_update` |
| Due date for retraining when no rule covers the learner | 30 days |
| Which recurrence applies when several rules cover a learner | The shortest |
| Pause during long leave (edge case) | Not built yet; assignments keep running during leave |
| How group and role changes reach rules | The hourly re-evaluation; unit membership changes apply at once |
| Overdue restrictions (open question) | None, only escalation |
| Package for training from onboarding and policies | `learning.mandatory`; without it a training step is skipped and a policy link is refused |
| Due date of a training step | The step's due time, or 5 working days when the step has none, like an attestation step |
| Due date of policy linked training | 30 days after the version came into force |
| Which policy versions assign training | Material ones, the first version included |
| How a course is chosen for a step or a policy | Paste its link or ID, as for lesson sources |

## Learning reports, team view and evidence export (spec 028, slice 3: T009; US5, FR-013, FR-014)

### Built
- `LearningReports`: for a course, people with an assignment counted as assigned, completed, overdue (past the due date and grace) and expired, per unit in the viewer's `learning.report` scope; a parent unit counts the units below it. Drill down to people with source, due date, completion time, version, best score and certificate.
- Evidence export: `GET /api/v1/learning/reports/courses/{id}/evidence[?unit=]`, a UTF-8 CSV (person, status, source, due date, completion time, version, score, certificate ID), audited as `learning.evidence_exported` with the course, the scope and the row count. Cells are protected against spreadsheet formulas.
- Team view: `GET /api/v1/learning/team` lists the viewer's direct reports and their courses; `POST /api/v1/learning/team/{userId}/assignments` gives a direct report a published course whose audience includes them (source `manager`, audited).
- Pages: `/learning/reports`, `/learning/reports/{id}`, `/learning/team`, linked from My learning when the viewer has reports or direct reports.
- Tests: `LearningReportTests` (2 tests: scoped counts, drill down, export and audit, learner and cross-tenant denial; direct reports only and manager assignment), `learning.spec.ts` (report and export).

### Smallest safe defaults
| Gap | Default |
|---|---|
| What counts as assigned | Everyone with an assignment of the course that was not withdrawn; completed, overdue and expired are parts of it |
| Who is a line manager here | The reporting line (010): direct reports only, no role or permission needed, like the leave team view |
| Which courses a manager can give | Published courses whose audience includes the report; due within a year, 30 days when not given |
| Export size | Up to 10,000 people in one synchronous CSV |
| Score in the evidence | The best quiz score of the assignment that completed |

## LMS connector (spec 028, slice 4: T010; US6, FR-015)

### Built
- An `ILmsConnector` contract in `Integrations.Contracts` and a generic `lms_rest` pack (framework key `integration.connectors`). Settings: the https address on the allowlist and its sign-in (secret manager references), the courses path with where the list is and where each course field is inside an item (ID, title, summary, launch address, language), the assignments path with where each assignment field goes in the body (dotted names make nested objects; only mapped fields are sent), the completions path with `{since}` and where each completion field is (course ID, learner work email, completion time, score), an optional next page link, and an optional idempotency header. Reads run inline through the connector runtime; an assignment is a durable connector job keyed by the TeamBeats assignment, so it is sent once.
- In Learning, `LmsSync` runs from the maintenance worker and from "Sync now" (`POST /api/v1/learning/manage/lms:sync`, `learning.manage` on the tenant and capability `learning.lms_connector`; status at `GET /api/v1/learning/manage/lms`). Each LMS course becomes a published course with no lessons, an `external_id` and a `launch_url`; a changed title or summary is published as a new version. Open assignments of LMS courses are pushed by the learner's work email. Completions complete the open assignment at the time the LMS gives, store the score as an attempt, mark obligations done and issue a certificate, so My learning, reports, the evidence export, onboarding and policies see them.
- Learners see an "Open in your LMS" button on an LMS course, or "LMS unavailable" while the LMS does not answer. Builders can change who sees an LMS course, its category, owner and open enrolment; its titles, lessons and publishing stay with the LMS (`conflict.learning.external_course`). The Connections wizard has an LMS group and form.

### Smallest safe defaults
- The catalogue is read every 6 hours and completions every 15 minutes, with one hour of overlap and 30 days back on the first read. A completion already recorded is not recorded again.
- A course the LMS no longer lists is left as it is for an administrator to retire, so a short or partial answer never withdraws assignments. A course an administrator retired stays retired when the LMS lists it again.
- New LMS courses target everyone with open enrolment off; administrators choose who sees them and assign them with rules.
- Learners are matched by active work email only. A completion for an unknown person or course is skipped. A person without an email has the push marked refused.
- A launch address must be https on an allowed host; items without one are not imported. Sign-in at the LMS is the LMS's own single sign-on with the tenant's identity provider; TeamBeats only links to it.
- "Unavailable" means the LMS did not answer the last time the sync called it, or there is no enabled LMS connection. Built courses, the catalogue and My learning keep working.
- A push that keeps failing after the runtime's retries shows in the connection's run history; it is not retried by Learning.

## SCORM and xAPI packages (spec 028, slice 5: T011; US7)

### Built
- A course builder with capability `learning.scorm` can upload a zip on a lesson of kind "package" (`POST /api/v1/learning/manage/courses/{id}/packages`, list with `GET`). The upload streams to a temporary file, is scanned before anything reads it, and is unpacked into tenant object storage under `learning/packages/{id}/`. The manifest (`imsmanifest.xml` for SCORM 1.2, `tincan.xml` for xAPI) gives the title, the launch file and, for xAPI, the activity ID. A course with package lessons publishes only while the tenant has `learning.scorm`.
- A learner starts a package with `POST /api/v1/learning/courses/{id}/lessons/{key}:launch`, which returns a launch address with a random token. Only the token's hash is stored. Content is served from `/api/v1/learning/content/{token}/...`, so it needs no session cookie and never sees the TeamBeats session. The token is the content's only credential: every content route except the CORS preflight answers 401 to an unknown or expired token.
- The player shows the content in an iframe sandboxed without `allow-same-origin`, and every content response carries its own sandbox content policy, `no-referrer`, `nosniff` and `no-store`. SCORM 1.2 pages get an in-house `API` script that reports to `__tb/runtime`; xAPI content gets an endpoint and an auth token in the launch query and uses the statements and state routes.
- Lesson status `passed` or `completed`, or an xAPI `completed` or `passed` statement on the package's own activity, completes the lesson through the same path as other lessons, with the score kept as an attempt. `failed` records a failed attempt. A package lesson cannot be marked done by hand (`conflict.learning.package_needs_launch`).

### Smallest safe defaults
- SCORM 1.2 and xAPI (Tin Can packages) only. SCORM 2004, AICC and cmi5 packages are refused with `validation.learning.package_version`.
- Upload limit 500 MB, at most 10,000 files and 2 GB unpacked, with a compression ratio check; a path holding `..` is refused, and backslashes and a leading slash are normalised first. Manifests are read with DTDs prohibited.
- A launch is valid for 8 hours. A new launch carries over the saved suspend data and xAPI state, so the learner resumes where they stopped. Suspend data is capped at 4,096 characters as in SCORM 1.2, and only the standard `cmi.core` and `cmi.suspend_data` keys are stored.
- A pass counts once per launch. The xAPI actor is an account on the tenant host with the TeamBeats user ID, never an email, so no personal data reaches the content.
- Content responses allow any origin without credentials, because the sandboxed frame has an opaque origin; the token is the only key and it ends with the launch.
- A package belongs to one course. Changing the package of a published lesson is a new course version, like any other source change.

## Learning performance and evidence (spec 028, slice 6: T012)

### Built
- `LearningPerformanceTests` writes 20,000 active members into the tenant, publishes a course and creates one rule for everyone through the API, then times the player for a learner. Locally the rule gives the course to 20,001 people in about 35 seconds against the 5 minute target, and the player's p95 is about 10 ms against 2 seconds. The first run took about 75 seconds locally and over 100 seconds on CI, past the HTTP client's limit; a whole rule now reads primary units and each unit's calendar once, writes the grace period with the assignment, and tells learners in one notification per due date.
- `specs/028-learning-and-training/evidence.md` maps every requirement to its tests. The OpenAPI document was regenerated with slices 4 and 5.

### Follow-ups
- A rule is still applied inside the request that creates it, with a few statements per learner. That meets the target with room to spare, but if tenants grow well past 20,000 people per rule, the first application should move to a background job with the rule showing "assigning" until it is done.

## Project and team spaces (spec 029, slice 1: T001 to T003; US1)

### Built
- A `Spaces` module with its own schema (`spaces.setting`, `template`, `space`, `space_member`, `task_list`, `task_column`, `task`, all with forced row level security) and the routes under `/api/v1/spaces`: rights, templates, the directory (`?scope=mine` for my spaces), create, get, change, `:archive`, `:restore`, `:join`, `:leave`, members, and `manage/settings` and `manage/templates` for `spaces.manage`.
- Permissions `spaces.create`, `spaces.manage`, `spaces.view`, `spaces.contribute` and `spaces.own`. The last three are only ever held through resource scoped built-in roles (`space_owner`, `space_member`, `space_viewer`) that Spaces gives through `IAuthorizationProvisioning.SetResourceRoleAsync`. Role administration refuses to assign them (`validation.role.module_assigned`) and a custom role cannot carry those permissions. A new role `spaces_administrator` holds `spaces.create` and `spaces.manage`; employees hold `spaces.create`.
- Capabilities `spaces.projects`, `spaces.guests` and `spaces.max_spaces` in every package except Workplace (Workplace Plus allows 200 spaces, larger packages have no limit).
- Built-in project and team templates plus up to 50 tenant templates; a template gives lists, board columns and starter tasks once, and later changes or retiring never touch spaces already made (FR-012).
- Spaces are a search source (kind `space`) filtered by the same reading rule as the page. Members are told when they are added (category `space_activity`). When the last owner leaves the company, ownership passes to the creator's manager, or the spaces administrators are asked.

### Smallest safe defaults
- Public spaces are readable by everyone in the tenant. Private spaces appear in the directory with name, description and owners, but only members read them. Secret spaces are invisible to anyone who is not a member.
- The tenant administrator and spaces administrators see a space's metadata and members so they can archive, restore and reassign owners, but they do not read private or secret content unless they are members.
- Space rules default to everyone with `spaces.create` creating spaces, guests off and 90 days of guest access (7 to 365).
- Built-in template names and starter content use the tenant's default language at the time the space is made.
- A space always keeps at least one owner (`conflict.spaces.last_owner`). Joining is allowed only for public spaces that allow it.
- An archived space is read only and leaves the directory, but stays in my spaces and the administrators' list.

## Space tasks, milestones and status (spec 029, slice 2: T004 to T007 backend; US2, US3, US7)

### Built
- Tasks with assignees, due date, priority, labels, a checklist and comments (`spaces.task`, `task_assignee`, `checklist_item`, `task_comment`). `GET /api/v1/spaces/{id}/tasks` returns the lists, their columns and every task, so the list view and the board read the same data; `:move` places a task in a column at a position, and `:complete` and `:reopen` move it to the list's first done or open column. A done column completes the task and leaving it reopens it. Members add lists (with To do, In progress and Done in the tenant's language) and columns.
- Members and owners change tasks; viewers and people who only read a public space are refused by the server (`authz.denied.spaces_contribute`), and archived spaces are read-only. Edits carry the task version (`conflict.spaces.task_changed`).
- Milestones (`spaces.milestone`) count their linked tasks; when all are done the milestone is reached once and `MilestoneReached` is published, and a reopened task opens it again. Owners set the status (on track, at risk, off track, done) with a note; every entry is kept in `spaces.status_entry`, the members who can read the space are told and `SpaceStatusChanged` is published. `GET /{id}/overview` gives the status with its history, milestone progress and overdue tasks.
- `GET /api/v1/spaces/my-tasks` lists the viewer's open tasks across the active spaces they are still in, by due date with the space name (`?done=true` adds completed ones). `GET /{id}/tasks.csv` exports the tasks for readers with spreadsheet formula protection.
- Notifications through 015 in category `space_activity`: assignment to the new assignees and completion to the task's watchers, each grouped per space and kind, and never to the person who acted or to anyone who cannot read the space. `SpacesMaintenanceWorker` sends the day-before reminder at 09:00 in each assignee's time zone (their unit's working calendar), once per due date.
- Tasks are a search source (`space_tasks`, kind `task`) with the space's reading rule. Events `TaskAssigned` and `TaskCompleted` are published.
- Web pages under `/spaces`: the directory with my spaces (and all spaces for administrators), a new space form, and per space the overview (status, history, milestones, overdue tasks), tasks as a board or a list with a "Move to" menu as the keyboard alternative to dragging, the task page with checklist and comments, members and settings; `/spaces/manage` holds the rules and retires templates. My tasks is a tab of the task inbox at `/tasks?tab=mine`, shown when `GET /api/v1/spaces/rights` says spaces are in the plan (`enabled`). `tests/Acceptance/specs/spaces.spec.ts` covers the flow in English and Arabic with axe.

### Smallest safe defaults
- A task's watchers are its assignees and the person who made it; there is no separate follow button yet. Status changes go to every member; spec 029's "followers" are the members until a follow feature exists.
- Only owners and members hold tasks, guests among them when they are invited as members (section 17); viewers cannot be assigned.
- A task belongs to at most one milestone (a column on the task instead of the plan's `milestone_task` table, which is enough for one-to-many).
- Limits: 5,000 tasks, 20 lists, 12 columns per list, 50 milestones per space; 20 assignees, 10 labels of 30 characters, 100 checklist items and 10,000 characters of description per task; comments of 4,000 characters.
- "Overdue" uses the viewer's own calendar day. A task that changes due date can be reminded again for the new date.
- My tasks appears in the 016 inbox as its own tab on the inbox page, not as inbox entries, so approvals are not buried under everyday work.
- Tenant template editing on the web is a follow-up: administrators retire templates there and create or change them through the API until the editor lands.

## Space library, discussion and task files (spec 029, slice 3: T008; US5, FR-006)

### Built
- Every space gets a library (spec 011) and a discussion (spec 023) when it is created, through `ISpaceLibraries` (Documents.Contracts) and `ISpaceChannels` (Engagement.Contracts). Spaces keeps their IDs (`library_id`, `community_id`) and `GET /api/v1/spaces/{id}` returns them to readers. A space made before this, or while the capability was not in the plan, gets them the next time a member opens it.
- The space is the audience. `ResourceDescriptor` gained an optional `Container`: a role assigned on a resource also applies to what it contains, so the space roles reach the space library (scope `space`) and the space community without copying members anywhere. Owners manage the library, members add files and viewers read (the space roles now carry `doc.view`, `doc.contribute` and, for owners, `doc.manage`; migration `0028`). In the discussion, readers of the space are members, contributors post and owners moderate. Everyone else gets "not found", and removing someone from the space ends their access at once.
- Permissions in a space library follow the space: breaking inheritance is refused (`conflict.documents.space_permissions`) and the ACL editor does not find it. The space community is not listed in the community directory, and joining, leaving, membership and archiving there are refused (`conflict.engagement.space_community`); its members are listed from the space roles.
- Renaming the space or changing its classification carries over to both. Archiving makes both read-only (`conflict.documents.library_archived`, no posting); restoring opens them again. Space posts carry the space's classification into search.
- Tasks link library files (`spaces.task_file`, `POST /{id}/tasks/{taskId}/files`, `DELETE .../files/{documentId}`). The caller must be able to open the file; each reader sees only the linked files they can open, always at the version readers get now (through `IDocumentReferences`).
- Web: the space's Files page shows the library with upload, the Discussion page shows the channel feed, and the task page lists linked files with a picker from the space library. The e2e spec uploads a file, posts in the discussion and links the file to the task.

### Smallest safe defaults
- The discussion is for members only, also in a public space: people who only read a public space see its tasks and files list but not its discussion, as spec 029 FR-006 names the space's members as the channel's audience.
- A space library has no owning unit and no ACL of its own, so tenant and unit roles never reach into it, matching the space itself (section 7).
- One general channel per space. Owners can add more channels in the discussion as in any community.
- At most 20 linked files per task.

## Team calendar (spec 029, slice 3: T011; US6, FR-009)

### Built
- `GET /api/v1/spaces/{id}/calendar?from&to` (up to 62 days, the viewer's current month by default) gives readers the space's events (`spaces.space_event`), milestones and task due dates in the range, and the members' approved leave from `IHrCalendarFeed` (spec 022). The leave type is always dropped, and pending leave never shows, even where the viewer could see it elsewhere.
- Members and owners add, change (with the version, `conflict.spaces.event_changed`) and delete events; viewers read. Events are audited as `spaces.event_changed`.
- `GET /{id}/calendar/availability?date` reads the members' free and busy for one day through `IAvailability` (spec 018) as the viewer, so Outlook's own sharing decides; only status blocks come back, never subjects. When the viewer's Outlook is not connected, needs a new sign-in or free and busy is off, the state says so and no one is listed.
- Web: the space's Calendar page shows the month with events, milestones, due dates and members away, a list of the month's events with a form to add one, and a free and busy panel for a chosen day.

### Smallest safe defaults
- Events have a title, an optional description, a start and an end (or all day) and last at most 31 days; at most 1,000 per space. There is no recurrence and no RSVP yet, and events are not sent to Outlook.
- Free and busy is read for the first 30 other members of the space, one day at a time.

## Guests (spec 029, slice 4: T009, T010; US4, FR-007, FR-008)

### Built
- Owners invite partners by email (`POST /api/v1/spaces/{id}/guests`, step-up) while the plan has `spaces.guests` and the tenant's space rules allow guests. The partner gets an invitation email through `IExternalEmail` with a link to the space; a relay that is down is tried again by the spaces worker, five times at most. The audit names the invitation, never the address.
- Tenancy.Contracts `IGuestInvitationSource`: at a first sign-in that matches no administrator appointment and no member domain, an identity with a pending invitation becomes a guest membership (never the default Employee role), and Spaces turns each invitation into a guest member of its space with its end date. Someone already a guest of the tenant is added at once with one identity across spaces.
- Guests see and open only the spaces they are invited to; any other space, public or private, is not found. They have no directory, search outside their spaces or space creation, because guests hold no tenant role. Guests are labelled on the members page with their end date.
- Owners renew access (step-up), cancel invitations and see each invitation's email state on the members page. Guests are members or viewers, never owners.
- Classified files (FR-008): Documents closes Restricted files to guests in every library (spec 026 FR-011) and Confidential files unless the tenant's rule (`guestConfidential` in the space rules) and the space's owner both allow them; closed files are left out of lists and search and refused when opened (`authz.denied.documents_guest_classified`). Spaces keeps the library's flag in step through `ISpaceLibraries.SetGuestConfidentialAsync`.
- The spaces worker tells owners seven days before a guest's access ends, once per end date, and ends it on the date: the guest leaves the space (`spaces.guest_expired`) and the owners are told; a guest left in no space is disabled and signed out. Invitations nobody took up lapse on the same date.

### Smallest safe defaults
- Guests join as viewers unless the owner picks member. Access lasts the tenant's guest days (90 by default) and an owner can give at most that; renewal starts again from today.
- An address in the tenant's member domains (the single sign-on provider's just-in-time domains) is refused as a guest, and so is anyone who already has a member account.
- At most 50 guests and open invitations per space.
- Turning guests off in the tenant's rules stops new invitations and pending sign-ins; guests already in spaces keep access until their date.
- Until spec 026 builds the reduced guest shell, a guest signs in to the normal shell; every page outside their spaces refuses them.
- Guests sign in through the tenant's own provider (for example as Entra business-to-business guests the tenant admitted), so the tenant's identity team still adds them there; TeamBeats holds no guest passwords.

## Spaces performance, demo data and evidence (spec 029, slice 5: T012)

- `SpacesPerformanceTests` adds 500 tasks to one board and 200 open tasks for one person across 10 spaces through the API, then measures the board (target 1.5 seconds p95) and My tasks (target 800 ms p95). Locally they take about 22 ms and 32 ms.
- The demo package gains `09-spaces.json`: a private finance rollout project with members, milestones, tasks and an at risk status, and a public People team space. Each space is created by its owner and found again by its name in the tenant's default language.
- `specs/029-project-and-team-spaces/evidence.md` maps every requirement to its tests.
- `/spaces/manage` now creates and changes tenant templates as well as retiring them: a template has a name, a kind and one or more task lists, each with its board columns and starter tasks one per line. In the editor the last column of a list is its done column and starter tasks start in the first column; templates made through the API with another done column take that layout when changed here.

## Workplace booking (spec 031, slice 1: T001 to T004; US1, US2)

### Built
- A new `Booking` module (schema `booking`, forced row-level security) with `Booking.Contracts` events `BookingConfirmed`, `BookingCancelled`, `BookingReleased` and `CheckedIn`. Permissions `booking.book` (employees), `booking.manage` and `booking.report`, the built-in roles Facilities administrator and Facilities analyst, and the capabilities `booking.rooms`, `booking.desks`, `booking.resources` and `booking.reports` (Workplace Plus and above).
- Sites, floors, zones and resources of the four kinds, each resource with an opaque 128-bit code for its QR label, unique across tenants and found only inside the scanning user's tenant. Facilities administrators manage a site within the scope of its org unit, or tenant-wide for a site without one; others are told the site is not found.
- FR-002: a room may carry its email address. Integrations keeps a cache of the tenant's Microsoft 365 room mailboxes (`integrations.room_mailbox`, behind `IRoomMailboxes`) and a room whose address is in it is refused. The cache is filled by the spec 018 Rooms feature, so until that is built it stays empty.
- Rules (FR-003) for a resource, for a kind at a site, or for a kind in the tenant: who may book (units, with their child units), days ahead, shortest and longest booking, opening hours on working days, a gap after each booking, active bookings per person, overnight, and how long a series may run. They are checked on the server for every booking, and search shows only what the person may book.
- Booking (FR-004 to FR-006): a booking is a date and times in the site's time zone. A PostgreSQL exclusion constraint (`btree_gist`) on the resource and the held time range of live bookings lets only one of any number of parallel attempts succeed. Daily and weekly series skip the site calendar's non-working days (spec 022 `IWorkingCalendar`) and list the holidays they left out; a series that meets a taken day names every taken date and books nothing.
- Confirmations and cancellations notify through spec 015 under the new Bookings category, and each booking has an iCalendar file (`GET /bookings/{id}/calendar.ics`). Removing a resource or a site cancels its future bookings and tells the holders with a link to the same zone (FR-015); a person whose membership ends loses their future bookings without a notice (FR-016). Facilities administrators cancel any booking at their sites with a reason, which is audited and told to the holder.
- Problem details now fill placeholders in the detail text from simple extension values, so the active booking limit shows its number.
- Web: Book a space (`/booking`), My bookings and a booking page, and for facilities administrators the site list, the site page with floors, zones, resources and their rules, and a printable labels page.

### Smallest safe defaults
- Spec 002 has no locations, so a site is defined in Booking: names per language, an optional directory location key (spec 010), an org unit for scope and working calendar, a time zone (the tenant's by default) and opening hours (08:00 to 18:00 by default).
- Default rules: 14 days ahead, 15 minute slots, within opening hours on working days, no gap. Rooms up to 8 hours and 10 active bookings per person; desks and parking up to 12 hours and 3 active bookings; equipment up to 7 days, overnight and outside opening hours, 3 active bookings, and no check-in. A series counts once against the limit and may run up to 90 days; only its first day is checked against how far ahead the rules allow.
- Notifications cannot carry files, so the confirmation links to the booking, which offers the calendar file.
- Colleagues see a taken slot as booked without a name unless the tenant turns on showing names (the open question default); facilities administrators of the site always see names.
- Reports (US6) follow in the next slice.

## Workplace booking (spec 031, slice 2: T003 QR images, T005 to T007; US3, US4)

### Built
- Restricted resources (FR-007): a resource may need approval and name up to 10 approvers. Its bookings are held as tentative until the approval hold of the settings runs out (24 hours by default) or the booking starts, whichever comes first, and the slot cannot be taken meanwhile. The approval runs on the spec 024 engine with one route per resource (`booking.approval:{resource}`, source `booking_route`), so it shows in the spec 016 task inbox through a new `ITaskSource`. A series waits on one approval for all its occurrences. Approved, the bookings are confirmed and `BookingConfirmed` is published; rejected, the slot frees and the holder sees the approver's reason; cancelling a request, or the holder leaving, withdraws its approval.
- Check-in (FR-008): from the booking, by scanning the resource's code (`GET /scan/{code}` and `POST /scan/{code}:check-in`, page `/booking/scan/{code}`), or at a site's kiosk page (`/booking/kiosk/{site}`, the signed-in person's bookings there today). Only the person booked for checks in, within the window from the settings' minutes before the start to the grace period after it (or after the booking was made, when that was later). `CheckedIn` is published.
- The QR labels now carry an SVG QR image (`GET /manage/resources/{id}/qr.svg`, facilities administrators) of the full check-in address on the tenant's host.
- `BookingMaintenance`, every minute per tenant in the worker (and the development host): tentative holds that ran out expire and the holder is told; holders are told once when check-in opens; bookings of resources with check-in on that nobody checked in to by the end of the grace period are released (`BookingReleased`) and the holder is pointed to the same zone. Resources with check-in off are never released.
- The no-show rule (FR-010) is tenant configuration in the booking settings: on reaching the limit within the window (3 in 30 days by default) the person is reminded once per window; when the tenant turns restriction on, they may then book only the set number of days ahead (1 by default) for the set number of days (14 by default). Search and booking both apply it.

### Smallest safe defaults
- The spec names a resource approver without saying who it is when none is named, so the facilities administrators of the site can always decide as well; a request never waits on an approver who left. The person booked for and the person who booked never decide.
- A kiosk is a page the person signs in to on the site's device; a separate kiosk device sign-in is not in the spec.
- The labels page uses the QR images; the page itself prints them through the browser.
- New package `QRCoder` (MIT): the stack has no QR encoder, and a QR code needs Reed-Solomon error correction and masking that are easy to get wrong by hand. It draws SVG on the server with no native dependency.

## Workplace booking (spec 031, slice 3: T008; US5)

### Built
- Booking on behalf (FR-011): `POST /bookings` takes `forUserId`. It is allowed only while the person has delegated to the caller today (spec 016 delegation, through `IDelegations`). The booking is the person's own: their rules, units, fixed desk and limits apply, they are told who booked it (`booking.booked_for_you`), and both of them see and can cancel it.
- Team booking (FR-012): `POST /bookings:team` books one resource of a kind for each person at a site, in a zone when given, at the same time. Each person gets an individual booking under their own rules and limits; a person who cannot be booked is listed with why (over their limit, nothing free, not in the team, or their rules) and the others are still booked. A person with a fixed desk at the site gets that desk.
- Web: Book a space offers "book for someone who delegated to you" and a "book for a team" section with one person picker per member and the outcome for each.

### Smallest safe defaults
- The spec does not say who may book for a team. The booker may include themselves, people who delegated to them, and anyone below them in the reporting line (spec 010), up to 50 people.
- Booking on behalf needs a delegation; being someone's manager is not enough on its own, as FR-011 asks for a delegation relation.

## Workplace booking (spec 031, slice 4: T009, T010; US6)

### Built
- Usage reports (FR-014): `GET /reports/utilization?site=&from=&to=&groupBy=` gives, for one site and up to 92 days, the resources, bookings, check-ins, no-shows and cancellations with booked, used and available hours, the utilization and the no-show rate, by resource, zone, site or week, with the site total. Only counts and hours are returned, never who booked. `GET /reports/sites` lists the sites the viewer may report on.
- Access: the `booking.reports` capability (workplace_plus) and `booking.report` in the site's scope (facilities analysts and facilities administrators). Anyone else is told the site is not found.
- Web: a Space usage page (`/booking/reports`), linked from Book a space for people with the report right, with the site, dates and grouping, the totals and a table.
- Performance (section 15): `BookingPerformanceTests` times search and the report at a site with 1,000 desks, half of them booked; both stay far under 700 ms p95 (about 30 ms locally).

### Smallest safe defaults
- Utilization is used hours over available hours. Used hours are checked-in hours, or confirmed hours for resources with check-in off, as nothing else shows they were used. Available hours are the site's opening hours on each working day of the site's calendar (spec 022), for every resource active in the range; a resource removed during the range still counts.
- The no-show rate is no-shows over bookings that needed a check-in (checked in plus no-show). Cancelled, rejected and expired bookings count only as cancelled.
- A booking counts on the day it starts in the site's time zone; its hours are clipped to the range. Weeks start on Monday.
- The spec allows a facilities administrator to see names on a single booking; the report itself never shows them, and the booking page already does for administrators.


## Workplace booking (spec 031, slice 5: closed days, time grid, retention, telemetry)

### Built
- Closed days (section 4 edge case, FR-015): a facilities administrator closes a site for a day with a reason (`POST /manage/sites/{id}/closures`, listed with `GET` and opened again with `DELETE /manage/sites/{id}/closures/{date}`). That day's live bookings are cancelled and the holders told the reason with a link to find another space; nobody can book the site that day, search finds nothing, the day shows closed and a series leaves it out. Audited as `booking.site_closed` and `booking.site_reopened`.
- Holidays (section 12, consumes holiday calendars from 022): Booking handles `WorkingCalendarChanged`. On each changed date that is now not a working day for a site's calendar, the bookings there of resources whose rule keeps to working hours are cancelled and the holders told (`booking.holiday`).
- Time grid (section 10): each result on Find a space can show the resource's day as slots of the opening hours. Each slot is a button that reads its time and whether it is free (with the name only where names are shown), works with the keyboard and runs right to left; a free slot sets the time to book.
- Retention (section 8): the booking job clears who held, made and decided a booking, and its title and note, once it ended more than a year ago, so only counts remain for the reports. Old series lose their holder too, and no-show notices over a year old are deleted.
- Telemetry (section 13): `booking.activity` counts booked, requested, approved, rejected, expired, checked in, released and cancelled by resource type and site. It is an unkeyed analytics event: no person, unit, locale or time finer than the day.

### Smallest safe defaults
- A closed day refuses every booking at the site, equipment too; a holiday cancels only resources that keep to working hours, since equipment may be booked outside them.
- Opening a closed day again does not bring cancelled bookings back; the holders were already told.
- A closure can be set from today up to a year ahead, with a reason of up to 500 characters that the holders see.
- Cleared bookings keep the empty id as holder, as the columns are required; nobody sees them as theirs.
- The grid shows 30 minute slots whatever the tenant's slot length, so a day fits on a phone.

## Mobile app (spec 030, slice 1: T001 to T004; US1, US5)

### Built
- Installable app (FR-001): `GET /api/v1/experience/manifest.webmanifest` gives the web app manifest for the signed-in viewer: the tenant's display name (spec 001), a short name of up to 12 characters, the accent and surface colours of the brand of the viewer's primary unit (spec 033) as theme and background colours, and its icons. The icons are the brand's favicon when it has one, an SVG of the tenant's initials on the brand colour (`GET /api/v1/experience/app-icon.svg`, also as a maskable icon) and the platform PNGs at 192 and 512 px. Both need the session and the `mobile.pwa` capability; the tenant comes from the session, so a manifest is never another tenant's.
- Capabilities (Entitlements migration 0025): `mobile.pwa` and `mobile.push` in every package, `mobile.offline` and `mobile.kiosk` in workplace_plus and enterprise_gov, `mobile.native_shell` (R3) in enterprise_gov.
- Service worker (FR-002, FR-019): `public/sw.js`, versioned by the release. It keeps only the app shell: the offline page, fetched without cookies so it holds nothing about anyone, the icons and the hashed build files. It never handles `/api` or `/dev` and never stores a page, so nothing is kept across people or tenants. Pages come from the network and the offline page answers when there is none.
- Updates (FR-002): a new release waits. The app shows "A new version is ready" with Reload and Later, hides it while a field has focus, and applies it only on Reload.
- Bottom navigation (FR-004, FR-005): Shell settings lets the experience administrator tick up to five global menu entries. The shell definition takes them as `mobileNav`, up to five global menu entries in order, validated as `validation.shell.mobile_nav`. The shell gives each viewer the chosen entries they may open as `mobileNav`. Under 40rem the shell draws them as a bar at the bottom with labels, 56 px tall targets, "More" for the rest of the global menu and logical properties so it mirrors in Arabic.
- Phone layouts (FR-003): `mobile.spec.ts` opens every page of R1 and R2 that needs no id at 360 px in Arabic and English and fails on any horizontal page scroll. The pages that scrolled sideways (tenant settings, users, plan, search settings, agent policy, HR integration) all had one cause: a grid whose single column grew to the widest child, such as a long option in a list or a wide table. Inside a page, children of grids and flex rows may now shrink below their content, fields never grow past their box, admin text wraps like page text, and segmented control labels wrap on phones.
- The add connection page sorted names with the device language, so the server and an Arabic browser put them in a different order; it now sorts with the page language, as does the connections list.

### Smallest safe defaults
- The spec does not say which brand an installed app follows when the tenant has several. It follows the brand of the viewer's primary unit, the same one the shell shows them, and the tenant's default otherwise.
- With no entries chosen, the bar shows the first four global menu entries. When there are more entries than places, "More" takes the fifth place, so the bar never has more than five targets.
- The offline page is stored in the language of the install. It is a static page with no personal content.
- In development the build files are not stored, since their names do not change between edits.

## Mobile app (spec 030, slice 2: T005, T006; US2)

### Built
- Web Push adapter (FR-007): `Integrations/WebPush/WebPushClient.cs` behind the `IWebPush` contract. Each message is encrypted for the browser's keys with aes128gcm (RFC 8291), so the push service cannot read it, and carries a VAPID token (RFC 8292, ES256, 12 hours, audience of that push service only). It uses only the .NET cryptography in the platform, so no library was added. The key pair comes from the secret manager as `Integrations:WebPush:PrivateKey` and `PublicKey` (base64url). Development and test hosts without one get a new pair per process; any other host without one has no push. The staging deploy creates the pair on its first run and gives it to the API and the workers.
- Push channel in Notifications (spec 015): `push` is a delivery channel next to email and Slack. Each category has a push choice (on by default), quiet hours apply as for email, and a delivery goes only to people with a device. A message holds the category name (marked when mandatory), a fixed short text and the link to the item, never the title or body of the item, so no Confidential content leaves the platform. Titles are clipped to 60 characters and the text to 120.
- Subscriptions: `GET /api/v1/me/push`, `POST /api/v1/me/push/subscriptions`, `POST /api/v1/me/push/subscriptions:remove`. Endpoints must be https on the default port on a known push service (FCM, Mozilla, Apple, Windows), so the platform never posts to an address a client chose. Keys must be 65 and 16 bytes. Each device is tied to the session that subscribed it. Publishes `PushSubscriptionCreated` and `PushSubscriptionRemoved`, and `mobile.push_optin` telemetry with only the platform family.
- Removal (FR-008): sign-out and session end, session revocation and leaving the organization remove the devices; a push service answer of 404, 410 or 403 removes the device at once. Sign-out also forgets the subscription in the browser. Timeouts, 429 and 5xx retry with the email backoff for up to two hours.
- The offer (FR-006): never on a first visit. A submitted leave request or service request dispatches the shared `tb:meaningful-action` event; the offer then appears once, only when the browser has not been asked yet and the person may get push. "Not now" is remembered on this device for 90 days for that person. The browser's own permission prompt opens only after "Turn on".
- Notification preferences: "Turn on for this device" and "Turn off for this device", a push column per category, and an explanation with email as the fallback where the device cannot receive push (iPhone and iPad outside an installed app) or the browser blocked notifications.
- Service worker: shows each message with the app icon and opens or focuses the app on the item's link when tapped. Only links inside the app are followed.

### Smallest safe defaults
- The spec does not say which categories push by default. Push is on for every category, as in-app is, but only reaches devices where the person turned it on.
- At most 10 devices per person; the oldest is dropped when an 11th subscribes.
- A message lives 24 hours at the push service. Urgent notifications are sent with high urgency.
- The VAPID contact defaults to an address in the reserved `.invalid` domain; staging sets it to the staging site address.

## Mobile app (spec 030, slice 3: T007, T008; US3)

### Built
- Offline bundle (FR-009, FR-010): `GET /api/v1/me/offline` in Experience composes the `IOfflineSource` contract (Content.Contracts). Content gives the live critical announcements targeted to the person and Policies gives the policies that apply to them, in the version they owe or else the one in force, each clause under its numbered heading. Both leave out anything above Internal and anything the person may not read now. The bundle keeps the newest items within the tenant's item and size limits, names its owner (tenant and person), carries server times for the sync and the expiry, and is sent with `Cache-Control: no-store`. It needs `mobile.offline` and a browser session.
- Key: each session gets its own 32 byte key, an HMAC of the tenant, person and session under `Mobile:OfflineKey` from the secret manager. Development and test hosts without one make one per process; other hosts without one answer `conflict.offline.unavailable`. The staging deploy creates it on its first run.
- Limits (section 6): the tenant security policy has `offlineMaxItems` (0 to 200, default 50), `offlineMaxMb` (1 to 200, default 50) and `offlineDays` (1 to 30, default 7), set on the security page by whoever may change the security policy. A change is audited as `security.policy_changed` and also as `mobile.offline_policy_changed` with the limits before and after.
- Device cache (FR-011): `lib/offline.ts` syncs on the first page of a visit, every five minutes after and when the connection comes back. Each item is encrypted with AES-GCM under the session key, kept as a non-extractable key in IndexedDB, and the whole cache is replaced at each sync, so withdrawn items and items the person lost access to are pruned. Sign-out, the sign-in page (reached after session expiry or revocation), a refused sync and expiry delete it.
- Offline page (FR-012): `public/offline-reader.js`, kept by the service worker, lists the kept items and opens one under "Offline. Last updated" with the sync time. It writes text only, never markup. Expiry uses the server time recorded at the sync; a device clock set back before that time wipes the cache. The page is now rendered per request so the reader script carries the response's nonce under the content security policy; the service worker keeps the page and its policy together.

### Smallest safe defaults
- The spec lists saved items and recent pages as offline content, but the platform has no saved items yet and recent pages would mean keeping arbitrary pages on the device. Slice 3 keeps critical announcements and policies only; saved items join when they exist.
- The experience administrator's choice of offline critical categories is not built: every critical announcement targeted to the person is kept. Recorded as a gap for the experience settings.
- "Mandatory policies" means the policies that apply to the person (spec 025), whether or not they have attested yet.
- One person's cache per tenant host: the next sign-in on the device starts with nothing.

## Mobile app (spec 030, slice 4: T009, T010; US4)

### Built
- Kiosk devices (FR-014): `identity.kiosk_device` holds the name, organization unit, location, allowed features, idle time (1 to 15 minutes, default 2) and whether people from other units may sign in. A device administrator (new role `device_administrator`, permission `device.manage`, section Admin, Kiosks) adds a kiosk and gets a one-time code (XXXX-XXXX-XXXX, 30 minutes). The tablet opens `/kiosk`, enters the code and gets an HttpOnly device cookie `tb_kiosk`; only its hash is kept. A new code for the same kiosk replaces the tablet: the old cookie stops working and its sessions end. Revoking ends every session on the kiosk at once, and the start screen checks its enrolment every 30 seconds, so a revoked kiosk stops offering sign-in within a minute.
- Badges and PINs (FR-015): `identity.kiosk_badge` keeps a hash of the badge code (TB plus 20 Crockford characters, printed with a QR) and a PBKDF2 hash of the PIN (4 to 8 digits). The code and QR are shown once. People sign in with the badge (scanner, typing, or the tablet camera where the browser has a barcode reader) or with their employee number once a PIN is set. A first badge chooses its PIN at the kiosk, typed twice. Five wrong PINs lock the badge for 15 minutes, audited as `device.kiosk_lockout`, and the owner gets a security notice. People set or change their own PIN in Settings after a fresh sign-in, which also lifts a lock; a device administrator can reset it.
- Who may sign in: an active account in the kiosk's unit or below (unless the kiosk allows other units) holding `kiosk.signin` at that unit (new role `kiosk_user`; tenant administrators hold both new permissions). Refusals are audited.
- Kiosk session (FR-016): a session with method `kiosk`, tied to the device, is valid only with that device's cookie, so a session cookie copied to another browser is refused. One person at a time: a new sign-in ends the previous one. The server ends the session after the idle time and the app signs out on the same timer with a warning 30 seconds before. The session cookie has no lifetime, so it ends with the browser. Sign-out and the start screen wipe browser storage, the offline cache, the device's push registration and every cache except the app shell.
- Feature block: `SharedDeviceRules` lists the API routes and pages each kiosk feature reaches (announcements, policies, leave, shifts, services, booking check-in), plus the home page, profile basics and the shell. A central guard after session authentication refuses any other route with `authz.denied.shared_device`, so typing the address of another feature does not help. The shell leaves out links to closed features and the home page leaves out placements whose data is closed. Everything needs `mobile.kiosk`.

### Smallest safe defaults
- The spec does not fix the badge format: an opaque random code with a QR, plus the employee number as a fallback. A first PIN can be chosen only by scanning or typing the badge code, not by employee number alone.
- Reissuing a badge keeps the PIN; an administrator reset clears it.
- The feature block is a list of routes in code reviewed with the spec, not tenant configuration; a tenant chooses only which listed features a kiosk offers.
- Notifications, offline reading and push offers are closed on kiosks.
- Desk and room check-in is a kiosk feature, as the floor and room kiosks of spec 031 already use it.
- Camera scanning is an extra where the browser offers it; nothing is recorded and the camera stops when the dialog closes.

## Mobile app (spec 030, final phase: T011)

### Built
- Mobile performance budgets (section 15): `tests/Acceptance/budgets/mobile.spec.ts` runs in the new CI job `mobile-budgets` on a production build (`E2E_WEB_MODE=production`, `next build` then `next start`) in English and Arabic. It applies the Lighthouse mobile profile in the browser: a 412 by 823 phone screen, slow 4G (150 ms round trip, 1.6 Mbps down, 750 kbps up) and a CPU four times slower. A first visit must reach its Largest Contentful Paint on the signed-in home page within 3 seconds. A later visit, with the service worker keeping the app shell, must paint within 1 second. Locally the two were about 0.85 s and 0.25 s.
- Install telemetry (section 13): the app sends `mobile.install` with the platform family when the browser reports the app installed. The event was declared but never sent before.
- Spec evidence in `specs/030-mobile-app-and-push/evidence.md`.

### Deviations
- The `lighthouse` package is not used. Version 13 depends on Sentry packages, one under the Functional Source License, which is not OSI approved (CLAUDE.md rule 6). The same mobile profile is applied through the browser's own throttling, so the budgets need no new dependency.
- Lighthouse no longer has a PWA installability category. What it checked (manifest fields, icons, a service worker that answers offline) is covered by `AppInstallTests` and `mobile.spec.ts`.

## Public API and MCP server (spec 035, slice 1: T001 to T004)

### Built
- Module `PublicApi` and `PublicApi.Contracts` (module map entry already in place). Tables `publicapi.setting`, `app`, `app_credential`, `consent` and `token_family`, all tenant-owned with forced row-level security, so an app ID, consent or token family of one tenant is never found under another. The scope catalogue of section 6 is `ScopeCatalogue` in the contracts, with the capability keys, the default consent rule and whether an app acting for a person, an app acting as itself, or both may hold each scope.
- Permissions `api.manage` (privileged) and `api.view`. Tenant administrators and integration administrators hold both, security officers hold `api.view`. Capability keys of section 9 are seeded: `api.public` and `api.mcp` in every package, `api.write_scopes`, `api.event_subscriptions` and `api.mcp_write_tools` from Workplace Plus, `api.max_apps` 2 and 20 (unlimited in Enterprise and Government), `api.monthly_calls` open until the commercial model sets it.
- Authorization server on OpenIddict 7.8 (Apache 2.0, ADR-0018). One server serves every tenant: the issuer is `https://<tenant host>/oauth`, metadata is at the RFC 8414 location `/.well-known/oauth-authorization-server/oauth`, and the endpoints are `/oauth/authorize`, `/oauth/token`, `/oauth/revoke` (RFC 7009) and `/oauth/jwks`. Other hosts, such as the platform host, have no authorization server. Only the authorization code flow with PKCE S256, refresh tokens and client credentials exist; plain PKCE, the implicit flow, the password grant and OpenID Connect ID tokens are not offered.
- Per-tenant keys (FR-001): `TenantKeys` derives an ES256 signing key and an AES key for codes and refresh tokens from one master secret (`PublicApi:SigningKey` from the secret manager), the tenant ID and a 90 day period, so every API instance derives the same keys and nothing is stored. The previous period's keys still validate for 24 hours after a rotation. Development and test hosts without the setting use a random secret per process; other hosts without it refuse every OAuth request. The staging deploy creates the secret on its first run.
- Sign-in (FR-003): without a session the authorization endpoint sends the browser to `/signin?returnTo=` with the whole request, so the employee signs in through the tenant's own identity provider; the sign-in page passes the return to single sign-on and the development form. Without consent for every requested scope the browser goes to the web page `/app-consent`, then back to the same request. Guests are refused unless the app allows them.
- Tokens (FR-006): access tokens are JWTs of 10 minutes by default (the tenant may set 5 to 60) with the tenant, app, principal, scopes, family and one resource audience (`<origin>/api/public` for apps, `<origin>/mcp` for MCP clients). Each sign-in is a token family: its code is redeemed once and each refresh writes a new generation, of which only a hash is kept. A code or refresh token presented a second time revokes the family (audited with reason `reuse_detected`). Refresh tokens last 30 days by default (the tenant may lower it). Application apps get access tokens only, and their credential is their family.
- Public API calls: `/api/public/v1/*` accept only bearer tokens; a cookie session never authenticates them. Each call checks the signature with the host tenant's keys, the issuer, the audience, the tenant claim, and then in the database the app, the token family or credential and the person's membership, so revocation, a disabled app and a disabled membership take effect at once (FR-007). A token of another tenant's issuer is audited there as `api.token_refused` with reason `cross_tenant`. `GET /api/public/v1/me` (scope `profile:read`) is the first public operation.
- Administration API under `/api/v1/api-access` (apps, client secrets with step-up and shown once, at most two active, 6 months by default and 12 at most, revoke, disable and enable, admin consent, settings) and employee API under `/api/v1/me/app-consent` and `/api/v1/me/connected-apps`. Removing a scope from an app or changing admin consent ends the families that relied on it; a disabled membership ends the person's families through `TenantMembershipDisabled`.

### Smallest safe defaults
- OpenIddict runs in degraded mode with no stores of its own: the module's tables are the stores, and handlers check clients, redirect URIs, secrets, consents and families. ADR-0018 says "custom stores on Npgsql"; this is the same idea with fewer moving parts, and no OpenIddict table exists.
- Signing keys are derived rather than generated and stored per tenant, so the secret manager holds one secret for the install instead of one per tenant and period. Rotation every 90 days with 24 hours of overlap follows section 8.
- Revocation is checked in the database on every public call rather than through a cache of at most 60 seconds; a cache can come with the request pipeline (T009) if the overhead budget needs it.
- Redirect URIs: HTTPS, or HTTP on a loopback address for native apps and MCP clients (RFC 8252); web apps never get plain HTTP. At most 10 per app.
- `prompt=consent` is not handled in R2: an employee who consented once is not asked again until they revoke. Cancel on the consent page returns `access_denied` to the app.
- The admin and employee pages for apps and connected apps come with T007 and T020; this slice has the consent page only.

## Public API and MCP server (spec 035, slice 2a: T005 and T006)

### Built
- Application principals (FR-011, spec 004 FR-017): `Authorization` gains the subject type `app` for role assignments and the role flag `assignable_to_apps`. An application app holds roles under its own app ID; a public API call with its token runs as the actor type `App`, and the authorization service then reads only the roles given to the app, never the default Employee role, groups, org memberships or inheritance rules. Grants are cached like a person's, so a removed role is gone within 60 seconds (in practice at once, because the grants version changes).
- `Permissions.ExcludedFromApps` names what no role for apps may hold: `authz.*`, `identity.*`, `security.*`, `api.*`, `audit.export` and `ai.policy_manage`. Defining such a role, assigning a role not marked for apps, or giving an app a role with a condition is refused, and a database trigger refuses the same through any other path. The loader also drops excluded permissions from an app's grants, so a role changed behind the rules still cannot widen an app.
- Built-in role App reader (`app_reader`, assignable to apps): `org.view`, `page.view`, `content.view`, `doc.view`, `people.view`. Custom roles can be marked assignable to apps with the custom roles capability. Roles go to an app through `POST /api/v1/authz/assignments` with `subjectType: app`, with the usual step-up and delegation rules, audited as `api.role_assigned_to_app`; `GET /api/v1/authz/assignments?appId=` lists them.
- Private key JWT (FR-005, RFC 7523): a credential is a client secret or a public JWK (`kind: public_key`), EC P-256 for ES256 or RSA of at least 2048 bits for RS256 or PS256. Only the public parameters are kept, with the RFC 7638 thumbprint as the hint; a key with private parameters is refused, and the same key cannot be registered twice. The token endpoint accepts `client_assertion` with `client_id`: signed by a live key of the app, issuer and subject the client ID, audience the tenant's issuer or token endpoint, expiry within 10 minutes, and a JWT ID that is used once (kept until the assertion would have expired). Metadata lists `private_key_jwt`.
- Expiry alerts (US1 scenario 6): `PublicApiMaintenanceWorker` runs every hour per tenant; each live credential of an active app with 14 days or less left notifies every holder of `api.manage` once (category `integration_alert`, template `publicapi.credential_expiring`, link `/admin/api/apps/{id}`). Used assertion IDs past their expiry are cleared on the same run.

### Smallest safe defaults
- An app's principal ID is its app ID; no separate `app_principal` table (the plan's link table) is needed while one app is one principal.
- Only App reader is marked for apps among the built-in roles; every other built-in role either holds an excluded permission or acts on things an integration should get only by a deliberate custom role.
- A client assertion must also send `client_id`. Sending a secret and an assertion together is a malformed request (`invalid_request`); any failed assertion check answers `invalid_client`.
- Assertions live at most 10 minutes; the spec is silent, and RFC 7523 leaves it to the server.

## Public API and MCP server (spec 035, slice 2b: T007)

### Built
- Administration section `api` (API and MCP access) at `/admin/api`, opened by `api.view`, whole tenant only. Tabs for apps (list and registration), consents and settings (the tenant switch, token lifetimes and the scopes people may allow on their own).
- App page `/admin/api/apps/{id}`: registration (name, publisher, description, scopes, exact redirect addresses, guests), disable and enable, credentials (public key preferred, or a secret shown once with a copy prompt; step-up as the API asks), the scopes allowed for everyone (admin consent) for delegated apps, and the roles of an app that acts as itself (only roles marked for apps, at one unit, a unit and below, or the whole organization).
- Consents for administrators (FR-029): `GET /api/v1/api-access/consents` (optionally `?appId=`) lists every live consent, people's own and the admin consent, for `api.view`; `POST /api/v1/api-access/consents/{id}:revoke` ends a person's consent for `api.manage`, with the same effect as the person removing it (their token families end at once, audited as `api.consent_revoked`).
- Employees: `/settings/connected-apps` lists the apps the person allowed, with what each may do, when and last use, and removes one at once. The settings page links to it only where the package has the public API.

### Smallest safe defaults
- The admin consent is changed on the app page, not from the consents list, so the scopes stay next to the app they belong to.
- Viewers (security officers) see the same pages; the API refuses their changes and the page shows its message, with no role checks in the frontend.

## Public API and MCP server (spec 035, slice 3a: T008, T009)

### Built
- The request pipeline of `/api/public`, in this order: the tenant's network rules (now ahead of the authorization server too, so they cover `/oauth` as FR-027 asks), the bearer token with its database checks, the tenant switch and `api.public`, the app's own address ranges, the limits, the monthly quota, then the call. Request bodies are at most 1 MB.
- Limits per minute (section 6): per tenant for all apps, per application app, and per delegated app and person; token requests per app at `/oauth/token`. Over-limit calls get 429 with `Retry-After` and `RateLimit-Limit`, `RateLimit-Remaining` and `RateLimit-Reset`; the tighter limit answers and the problem says which (`app` or `tenant`). A tenant may lower an app's limit (`callsPerMinute`, 0 returns to the default) and its own (`tenantCallsPerMinute`), never raise them; the installation sets `PublicApi:Limits:TenantCallsPerMinute`.
- Monthly quota through `api.monthly_calls`: only processed calls count; once used up the API answers 429 `entitlement.quota_exhausted` with `Retry-After` until the next month, and the web app keeps working. Holders of `api.manage` are told once a month at 80% and at 100% (templates `publicapi.quota_80`, `publicapi.quota_100`, linking to the usage tab).
- Per-app address ranges (`ipRanges`, CIDR, at most 20): tokens and calls only from them; refused calls are audited as `api.token_refused` with reason `app_network`, at most once a minute per app and address.
- Call log `publicapi.call_log` (operation as method and route pattern, status, error class, latency, app, person, correlation ID; no bodies or query strings), kept 90 days by the hourly maintenance. Usage page: the Usage tab of `/admin/api` from `GET /api/v1/api-access/usage?days=` (1 to 90, `api.view`).

### Smallest safe defaults
- Counters live in each instance with windows that start on the minute, and each instance counts its share of every limit: the limit divided by `PublicApi:Limits:Instances` (default 1), as the plan says. A shared counter can replace it later without changing the API.
- No revocation cache: every call checks the app, the family or credential and the membership in the database, which is stricter than the 60 seconds FR-007 allows.
- The monthly period is the calendar month in UTC, as the AI request quota counts it.
- Over-limit token requests get 429 with the OAuth error `temporarily_unavailable`, as RFC 6749 has no error for it at the token endpoint.
- Error classes in the call log: `rate_limited`, `quota`, `denied` (403), `not_found`, `client` (other 4xx) and `server`.

## Public API and MCP server (spec 035, slice 3b: T010 and the first T011 operations)

### Built
- The `PublicEndpoint` convention in `PublicApi.Contracts`: a module maps its public operations in its own project with `app.MapPublicApi(tag)` (under `/api/public/v1`, in the public OpenAPI document only) and declares one catalogue scope with `.RequireScope(scope)`. The filter checks the token holds the scope, that the caller's kind may hold it, and the scope's capability keys; the handler still asks the central authorization service. `PublicCaller` moved to the contracts so modules read the caller with `http.Caller()`.
- A test fails when a `/api/public` operation has no scope or one outside the catalogue, or when it lives outside `v1`.
- A separate OpenAPI document `/openapi/public-v1.json`, kept as `docs/api/openapi.public.v1.json` next to the internal one (the plan named `docs/architecture/`; `docs/api` is where the contracts already live). The internal document no longer lists public operations. The governance CI job runs `tools/openapi-breaking.py` against the document on the base branch and fails on a removed operation, parameter, property, enum value or success response, or on a new required input; `tools/openapi-breaking-test.py` checks the checker.
- Conventions of FR-015: cursor pages (`limit` 1 to 200, default 50, opaque `cursor`, `nextCursor` null on the last page), weak ETags with `If-None-Match` answering 304 on readable resources, `X-Request-Id` echoed on every public answer (the correlation ID, which a caller may set with `X-Request-Id`), and `Idempotency-Key` on operations marked `.Idempotent()`: the first answer is kept 24 hours in `publicapi.idempotency` and replayed with `Idempotency-Replayed: true`; the same key with another request is 422 `api.idempotency_key_reused`, and while the first call runs 409 `api.idempotency_in_progress`.
- Display text follows `Accept-Language` within the tenant's locales, else the tenant default, with `Content-Language` on the answer (FR-016).
- `org:read` (spec 002): `GET /api/public/v1/org-units` (optionally `parentId`) and `GET /api/public/v1/org-units/{id}`, owned by `Tenancy`. `GET /api/public/v1/me` now uses the convention too.

### Smallest safe defaults
- Idempotency is applied in the pipeline around the endpoint, so modules need no code for it. A hold whose call never finished is released after 5 minutes; answers over 64 KB and server errors are not kept, so such a call runs again on retry.
- The idempotency hash covers the method, the path and the body bytes, not the query string.
- Org units are paged in memory after the authorization filter: a tenant has at most some thousands of units, and the order (level, code, ID) is stable.
- The public document has no security scheme section yet; the developer page (T019) adds it with the OAuth metadata.


## Public API and MCP server (spec 035, slice 3c: T011 read scopes of content, documents, people and policies)

### Built
- `content:read` (spec 009), in `Content`: `GET /api/public/v1/content` (the news feed, with `siteId` and `type`) and `GET /api/public/v1/content/{id}` (the live version with its structured body, in the caller's language or the fallback, and a link to the reading page).
- `documents:read` (spec 011), in `Documents`: `GET /libraries`, `GET /libraries/{id}/documents` (with `folderId` and `recursive`, newest first), `GET /documents/{id}`, `GET /documents/{id}/versions` and `GET /documents/{id}/content`. The file is served on the call itself after the same checks as a download link, so no link outlives the token; confidential and restricted files are audited and every file counts towards download volume alerts (spec 026), for the app when an app reads as itself.
- `people:read` (spec 010), in `People`: `GET /people` (search with `q` and `unitId`), `GET /people/{id}` and `GET /people/{id}/org-chart`, with the fields the field policy shows the caller.
- `policies:read` (spec 025), in `Policies`: `GET /policies` (policies in force) and `GET /policies/{id}` (the version in force with its clauses, and for a person their own attestation status).
- `ITenantContext.RequirePrincipal()` returns the person or the application app, for reads both may make; the module still decides what the caller sees through the central authorization service.

### Smallest safe defaults
- An application app is not a member of the tenant, so what every member may view (content and libraries at tenant visibility) is not open to it: the evaluator gives the `tenant_visibility` reason to people only, and an app reads only what its own roles cover, at their org scope (spec 035 section 7). An app with App reader at the whole organization still reads everything.
- The feed is targeted for a person as in TeamBeats. An app has no audience facts, so it gets every live item its roles let it read.
- An app's directory is the people placed in the units its own `people.view` covers. It sees only the fields every employee sees, since it is in no unit and manages no one, and it is never told who is on leave.
- Policies apply to people through their audience. An app is in no audience, so it reads the published policies whose unit its own `content.view` covers (the whole tenant for a tenant-wide policy), never drafts, and it owes no attestation. No new permission was added for this.
- Lists the owning module pages by offset (documents, people, policies) carry the offset inside the opaque cursor. The people search keeps its own page cap of 50, so a page can be shorter than `limit` with a `nextCursor`.

## Public API and MCP server (spec 035, slice 3d: T011 search, assistant, task inbox and news drafts)

### Built
- `search:query` (spec 012), in `Search`: `GET /api/public/v1/search` with `q`, `kind`, `from` and `to`, 20 results a page with the page in the cursor. Each owning module filters the candidates for the caller, as in TeamBeats.
- `assistant:ask` (spec 014), in `Agents`, for delegated apps: `POST /api/public/v1/assistant/answers` with a question and an optional conversation. The answer is cited and marked as AI generated. It is idempotent.
- `tasks:read` (spec 016), in `Services`, for delegated apps: `GET /api/public/v1/tasks`, the person's own inbox with a link to each task in TeamBeats. There is no operation to decide a task.
- `content:draft` (spec 009), in `Content`, for delegated apps: `POST /api/public/v1/content/drafts` creates a news or page draft in the person's name, with their authoring rights at the site. It is never published, and it is idempotent.

### Smallest safe defaults
- In search, an application app finds the people its own `people.view` covers. It finds no services, since the catalogue is offered by audience and an app is in no audience, and it finds no community posts, since it is a member of no community. Learning was already limited to people.
- A draft from the API has one language, the caller's unless `locale` says otherwise. Announcements are not drafted from the API, because the catalogue names news and pages.
- The 20 an hour limit for `ask_teambeats` (section 6) belongs to the MCP tool and comes with slice 4. The REST answer is held by the per app and per user minute limits and by the tenant AI budget.
- Services, requests and document uploads, for people and for apps, are the next slice. They need a rule for what an app reads in the catalogue and in whose name an app submits.

## Public API and MCP server (spec 035, slice 3e: T011 services and requests)

### Built
- `services:read` (spec 016), in `Services`: `GET /api/public/v1/services` with `q` and `categoryId`, and `GET /api/public/v1/services/{id}` with the form of the published version, names in the caller's language.
- `requests:read`: `GET /api/public/v1/requests` with `status`, newest first with a cursor, and `GET /api/public/v1/requests/{id}` with the data filed and the tracking. Attachments are listed by name only.
- `requests:submit`: `POST /api/public/v1/requests` with the service, the data and, for someone else, `onBehalfOf`. It is idempotent through `Idempotency-Key`.
- A new permission `service.request_read` and two built-in roles assignable to apps: Request reader for apps (`app_request_reader`: `service.request_read`) and Request filer for apps (`app_request_filer`: `service.request_read` and `service.request_on_behalf`). Migration `authz/0033` adds them to existing tenants; `services/0003` adds `requester_app_id` to requests.

### Smallest safe defaults
- A person sees the catalogue and their own requests as in TeamBeats, and files for themselves or, where the service and spec 016 allow, for others.
- An application app sees no service and no request without a request role (US1 scenario 3). With one it reads the requests filed under the units the role covers, by the unit stored on the request (the person's primary unit when filed), and the requests it filed itself (US1 scenario 4).
- An app's catalogue is the published services offered to someone in its units: no unit in the audience, or a unit the app covers or one above it. Groups and membership types say who may use a service, so they are checked for the person the request is for.
- An app is nobody, so it always files for one named person, with `service.request_on_behalf` at that person's unit from its own role, on a service that allows filing for others. The person becomes the requester, as if they had filed it, so it is theirs to follow and cancel; the request keeps the app in `requester_app_id`, the submitted event has no person as actor, and the audit names the app.
- HR values are never prefilled for an app, as for anyone filing for someone else. Attachments cannot be uploaded through the API in R2, so a form that requires one cannot be filed by an app.
- Search still finds no services for an app.

## Public API and MCP server (spec 035, slice 3f: T011 document uploads)

### Built
- `documents:write` (spec 011), in `Documents`: `POST /api/public/v1/libraries/{id}/documents` with `name`, `folderId`, `typeId`, `metadata` (a JSON object), `classification` and `publish` in the query and the file as the body, and `POST /api/public/v1/documents/{id}/versions` with `name`, `note` and `publish`. Both answer 201 with the document.

### Smallest safe defaults
- A person uploads with their own rights at the folder, and size, type and storage limits follow the library and the plan as in TeamBeats. The body limit of 1 MB for public calls is lifted to the library's limit for these two operations (section 6).
- An application app acting as itself never uploads (`documents.app_upload`, 403). A document has an owner and each version an author, and both are people; an app owner would leave nobody to check in an incomplete upload or receive a quarantine notice. The scope stays held by both kinds, so a later decision can open it without a breaking change.
- Uploads are not replayed by `Idempotency-Key`, because a replay keeps at most 64 KB and the body is read only once. Sending the same file to the library again is refused as a duplicate (409). A new version sent twice adds two versions.

## Public API and MCP server (spec 035, slice 4a: T013 and the read part of T014)

### Built
- The external AI caller type in `Agents` (spec 027 FR-025): tool definitions carry an `Exposable` flag, and the agent policy carries an `external` part with `enabled`, the list of allowed MCP clients each with a classification cap (`public`, `internal` or `confidential`, Internal by default, never Restricted), an optional list of tools offered to external clients, and `writeTools`.
- The external tool gateway (`IExternalToolGateway`): the kill switch, external access, the client on the list, the exposable flag, the token's scopes, the tenant policy and its rules (never `block`), the external tools list, entitlement, the call limits and the parameter schema, in that order. The tool then runs as the employee through the owning module, and the result and each source are checked against the client's cap. Every call, refused or executed, leaves a row in `agents.external_call`.
- Read tools, each in its owning module: `whoami`, `get_page`, `list_news`, `get_document`, `find_person`, `get_policy`, `search`, `ask_teambeats`, `list_services`, `list_my_requests` and `list_my_tasks`, mapped to scopes as in section 6.
- The MCP server at `/mcp` over Streamable HTTP with the official C# SDK (ADR-0018), stateless, behind the bearer pipeline: it needs `api.mcp`, a delegated token bound to the MCP resource and an app of kind `mcp_client`. A 401 names the protected resource metadata at `/.well-known/oauth-protected-resource/mcp` (RFC 9728). Tool results go back as structured content with `ok`, `error`, `classification`, `content` and `sources` with absolute links.
- An MCP client that the policy does not allow is refused at the authorization endpoint with `access_denied`, and the refusal is audited (`agent.external_call_refused`).

### Smallest safe defaults
- No MCP client is allowed until an AI policy administrator lists it. External access is on by default, so listing a client is the one step needed; turning it off refuses every client.
- Execution records live in `agents.external_call` and are purged with agent run records. Parameters are masked when what went back was Confidential; the content itself is never stored.
- The call limits are counted on the execution records: 60 calls a minute per employee and client, and 20 `ask_teambeats` calls an hour.
- A page, document or policy the employee may not read, or that does not exist, is refused as `tool.denied`; the client learns nothing more.
- `ask_teambeats` draws only on passages within the client's cap, and a conversation already above the cap is refused.
- Write tools (`submit_service_request`, `draft_news`) are neither listed nor called until the confirmation step inside TeamBeats is built in slice 4b. Resources (`teambeats://` URIs) also come in slice 4b.

## Public API and MCP server (spec 035, slice 4b: T014 confirmed write tools)

### Built
- Write tools for external AI clients: `submit_service_request` in `Services` (a form service request in the employee's own name) and `draft_news` in `Content` (a news draft, never published). They are offered only when the external AI client policy has `writeTools` on and the package has `api.mcp_write_tools`, and never when the tool's rule is `block` or needs an approver.
- A write tool call never acts. It becomes a pending action in `agents.external_action` with the exact parameters and their hash, and the employee gets a notification (approval waiting) and a task in the inbox, both opening `/ai-actions/{id}`. The card names the AI client, says the request came from an external AI client, and shows every parameter as sent.
- `GET /api/v1/me/ai-actions`, `GET /api/v1/me/ai-actions/{id}` and `POST /api/v1/me/ai-actions/{id}/decision` with `confirm` and the card's `hash`. Only the employee the client acted for sees or decides an action; to anyone else it is not found. Confirming runs the gateway checks again (kill switch, client, external access, write tools, scopes held at the call, schema, the hash) and then the tool with exactly the stored parameters; the decision resolves the notification.

### Smallest safe defaults
- The client learns the outcome by calling the tool again with the same arguments: still waiting, the confirmed result (for a day, so a retry never acts twice), or rejected, expired or failed. A closed outcome other than confirmed is told once; the next identical call asks the employee again.
- A pending action expires 15 minutes after the call. The clean-up marks it expired; the card and the client treat it as expired from that moment either way.
- A confirmed service request uses the action, not the parameter hash, as its idempotency key, so the same values confirmed again later make a new request.
- A form that the owning module refuses when the action runs leaves the action failed with `tool.invalid_arguments`; nothing is submitted.
- Results are kept with the action for a day for the client to fetch, then cleared; the action record stays as long as agent run records.
- Confirmation inside the AI client (MCP elicitation) stays in R3, as the spec says.

## Public API and MCP server (spec 035, slice 4c: T014 resources and the external AI client settings)

### Built
- MCP resources `teambeats://pages/{id}`, `teambeats://documents/{id}`, `teambeats://policies/{id}` and `teambeats://people/{id}`, offered as resource templates for the kinds the token's scopes cover (`content:read`, `documents:read`, `policies:read`, `people:read`). Each kind is read through a tool of the owning module (`get_page`, `get_document`, `get_policy`, and `get_person` in `People`) over the same gateway, so the kill switch, the client list, the classification cap, the call limits and the execution record all apply.
- `resources/list` lists the employee's news feed as pages, within the same filters. Tool results name each source's resource URI next to its link.
- The agent policy screen has an "External AI clients" part: external access on or off, write tools on or off, and the allowed client IDs, each with the highest classification sent to it.
- The web host forwards `/mcp` and `/.well-known/oauth-protected-resource/...` to the API, as it already did for `/oauth`, so an AI client reaches the MCP server at `https://<tenant host>/mcp`.

### Smallest safe defaults
- `get_person` reads a profile for the people resource only. It is not one of the section 6 tools, so it is never listed by `tools/list` and a `tools/call` for it is refused as unknown.
- A resource the employee may not read, that is above the client's cap, that is outside the scopes or that does not exist answers the same "resource not found" error (-32002), so a client cannot tell which.
- Documents, policies and people are not listed by `resources/list`: they are found through `search` and read by URI. Listing every document an employee may read would be large and add nothing the search does not give.
- The administrator types the client ID of the MCP client app from API access. Picking from the registered MCP client apps waits for the verified client list (slice 4d).

## Public API and MCP server (spec 035, slice 4d: FR-023 verified MCP clients)

### Built
- The platform's verified MCP client list (`publicapi.verified_mcp_client`, platform data). Each entry is the HTTPS URL of a client ID metadata document with the name, publisher, redirect URIs and scopes the platform operator recorded at the review. The platform operator keeps it at `GET`/`PUT /api/v1/platform/api/verified-mcp-clients`; each change is in the platform audit (`api.verified_client_changed`).
- The AI policy screen offers the active verified clients to add, next to the typed client ID for the tenant's own MCP client registrations, and shows each allowed client's name and publisher.
- An allowed verified client needs no registration by the API administrator. The first employee who connects it registers it for the tenant (`api.app_registered`, marked verified), after the external AI client policy allowed it; until then nothing is stored. A client the policy does not allow is refused before anything is registered.
- A verified registration always takes its name, publisher, scopes and redirect URIs from the list, so the API administrator cannot change them (`validation.api.verified_app_fixed`); IP ranges, call limits and disabling stay with the tenant. Blocking an entry on the platform disables every tenant's registration at once, tokens included.
- The consent screen says whether a client is verified by TeamBeats or registered by the organization, and the apps list marks verified clients.
- T015: the permission isolation, unsafe actions and data classification suites (now version 2) each gain an English and an Arabic case tagged `external_caller`: a request relayed from an AI app outside TeamBeats must still be refused, and an action must never run without the employee's confirmation. The gateway rules themselves (client list, scopes, cap, confirmation) are deterministic and tested in `PublicMcpTests`.
- Redirect URIs of native apps and MCP clients on a loopback IP literal match on any port, as RFC 8252 section 7.3 asks; scheme, host, path and query must still match. Every other redirect URI matches exactly.

### Smallest safe defaults
- The platform operator records the reviewed metadata; TeamBeats does not fetch the metadata document at sign-in. A document that changes later changes nothing until it is reviewed again, and no tenant request makes TeamBeats call out to a client's server. The plan's fetch through the spec 017 safe HTTP client is not needed for this and stays unbuilt.
- The verified list starts empty. The spec's first list (Claude, ChatGPT, Microsoft Copilot Studio, Visual Studio Code) is added by the platform operator after each security review, not seeded in code.
- Removing a client from the list is blocking it (`status = blocked`); entries are kept so the platform audit and tenant registrations still name them.
- A verified registration counts against the package's app limit. When the limit is reached the employee's sign-in is refused with `access_denied`.
- Verified client IDs are at most 200 characters, the length the external AI client policy accepts; token requests are counted per client ID up to that length.

## Public API and MCP server (spec 035, slice 4e: FR-023 dynamic client registration)

### Built
- `POST /oauth/register` (RFC 7591) on the tenant host, only where an API administrator turned on "AI clients may register themselves" in the API settings (off by default, audited with the other settings). Elsewhere the endpoint answers 404 and the authorization server metadata has no `registration_endpoint`.
- A client that registers itself becomes a pending public MCP client app with no creator (`self_registered`). While pending it is unknown to the authorization server and the consent screen. Enabling it on the app page is the approval; disabling it is the refusal. After approval, the external AI client policy must still allow it, as for any MCP client.
- Errors use the RFC 7591 codes: `invalid_redirect_uri` and `invalid_client_metadata`, plus `temporarily_unavailable` with Retry-After when over the limit.

### Smallest safe defaults
- Public clients only: `token_endpoint_auth_method` must be `none` (PKCE, no secret), with the authorization code and refresh token grants and the `code` response type. TeamBeats issues no client secret or registration access token, so a registration cannot be read or changed through the protocol afterwards; the API administrator manages it on the app page.
- Without a `scope`, a client gets the read scopes employees may allow alone that the MCP server serves and the package includes. Write scopes can be asked for, and as for any app they need an API administrator's consent.
- At most 5 registrations a minute per tenant and 20 pending at a time. Pending registrations count against the package's app limit.
- API administrators are not notified of each registration; pending clients show in the apps list with a "Waiting for approval" status.

## Public API and MCP server (spec 035, slice 5a: T018 emergency controls)

### Built
- "Revoke all tokens" on the app page (`POST /api/v1/api-access/apps/{id}:revoke-tokens`, `api.manage`): every token family ends at once and access tokens issued before now are refused at their next use, while the app stays enabled for new sign-ins (`api.tokens_revoked`). Disabling an app does the same and also refuses new tokens.
- The platform block (`publicapi.platform_block`, platform data) at `/api/v1/platform/api/blocked-clients`: a blocked client ID, of any kind, stops working in every tenant on its next request, tokens included. The tenant's app shows "Blocked by TeamBeats" and cannot be enabled until the block is lifted. Block and unblock are in the platform audit with the reason.
- Turning the public API off needs a reason, kept in the `api.tenant_switch_changed` audit entry. External AI access has its own switch in the agent policy, as before.
- A refresh token or code used twice (the family is revoked, as before) now also tells the person and the API administrators, and raises the security alert `api_token_reuse` (spec 026 US5) for security officers.
- Revoking an employee's consents is the existing consents tab.

### Smallest safe defaults
- Tenant administrators see a platform block on the app page and in the apps list. They are not sent a notification: the platform holds no list of which tenants registered a client ID, and finding them would mean reading every tenant's registrations. Verified clients blocked on the verified list behave the same way.
- Lifting a block restores the app as it was. Tokens issued before the block work again unless they were revoked or have expired; the platform operator asks tenants to revoke tokens when a secret may have leaked.
- Access tokens are refused at their next use after a revocation or block: every call reads the app registration, so there is no cache to wait for, well within the 60 seconds of US6.

## Public API and MCP server (spec 035, slice 5b: T016 and T017 event subscriptions)

### Built
- The public event catalogue in `PublicApi.Contracts` (`PublicEventCatalogue`): `ContentPublished`, `DocumentVersionPublished`, `PolicyPublished`, `RequestSubmitted`, `RequestCompleted`, `ProfileUpdated` and `OrganizationUnitMoved`, each version 1, each with the resource it is about, the read scope that resource needs and its public API path. PublicApi handles the internal events behind them; internal events are never sent as they are.
- A notification is the spec 017 webhook body (`id` = delivery ID, `type`, `occurredAt`, `data`) with `data` = `eventId`, `version`, `resourceType`, `resourceId` and an absolute `resourceUrl` on the tenant host. Nothing else, well under 4 KB. `ProfileUpdated` does not say which fields changed.
- Before sending, PublicApi asks each subscribed app, as itself, whether it may read the resource. The owning module answers through `IPublicResourceVisibility`, by running its own read for the public endpoint in a scope that is never committed. An app that may not read the resource gets nothing.
- Delivery goes through the new Integrations contract `IWebhookDelivery`. An endpoint is a connection of a new, unlisted pack `api_events` that sends exactly as the webhook pack does: signed with HMAC SHA-256 over timestamp and body, to an allowlisted HTTPS host, retried as a durable job, kept in the run history. The Connections page, connector catalogues, route steps, agent tools and connection alerts leave these connections out, and they do not count towards the connection limit.
- `publicapi.event_subscription` (tenant-owned, forced RLS). API administrators manage subscriptions on the app page and at `/api/v1/api-access/apps/{id}/subscriptions`, `/subscriptions/{id}:resume`, `DELETE /subscriptions/{id}` and `/subscriptions/{id}/deliveries` (the delivery log). An app with `events:subscribe` reads the catalogue at `GET /api/public/v1/events`, lists its own subscriptions at `GET /api/public/v1/event-subscriptions` and removes one with `DELETE /api/public/v1/event-subscriptions/{id}`.
- When a delivery fails or ends unknown and the first failure since the last success is 3 days old, the subscription is paused, its endpoint is turned off, `api.subscription_paused` is audited and API administrators get a notification (`api.subscription_paused.admin`). Resuming starts the count afresh (`api.subscription_resumed`). `api.subscription_created` and `api.subscription_deleted` record the app, the event type and the URL host.

### Smallest safe defaults
- Only an API administrator creates subscriptions in R2. The signing secret is a tenant secret manager reference, as for every webhook of spec 017, and an app cannot put one there; so through the public API an app reads the catalogue and lists and removes its own subscriptions, but does not add one. The `events:subscribe` scope is still required on the app registration for a subscription to be created or delivered.
- Only application apps subscribe. A subscription is delivered only while the app is active, not blocked, holds `events:subscribe` and the event's read scope, the tenant's public API is on and the package has `api.event_subscriptions`. Events that happen while any of these is missing are not kept for later.
- At most 20 subscriptions per app, and one per app, event type and URL.
- The `api_events` pack needs only `api.event_subscriptions` in the package, not the connector framework, since the tenant configures a subscription and not a connection.
- A paused subscription keeps its delivery log. Removing a subscription removes its endpoint and that log.

## Public API and MCP server (spec 035, slice 5c: T019 developer page and deprecation headers)

### Built
- The "Developer page" tab of API and MCP access (`GET /api/v1/api-access/developer`, `api.view`). It shows, on the tenant's own host, the public OpenAPI document (`/openapi/public-v1.json`, now served through the web host as well), the authorization server metadata, the MCP server and its protected resource metadata. It lists the public operations with their scopes, the scopes, the event catalogue, and the MCP tools and resources, each only as far as the package includes them. It also lists deprecated operations with their replacement and the date they stop working.
- `.Deprecated(announced, sunset, replacement)` in `PublicApi.Contracts` for public operations. Every answer, errors included, carries `Deprecation` (RFC 9745, `@` and Unix seconds), `Sunset` (RFC 8594) and a `Link` to the replacement with `rel="successor-version"`. The OpenAPI document marks the operation deprecated. A sunset less than 12 months after the announcement, or a replacement outside `/api/public`, fails at startup.

### Smallest safe defaults
- No public operation is deprecated in R2, so the list is empty and no response carries the headers yet.
- The OpenAPI document is the whole public v1 contract, the same for every tenant (platform data). The developer page narrows it to the package by listing only the operations whose scope the package includes.
- Operation paths, scope keys, event types and tool names stay in English. Scope and tool descriptions are in the reader's language.
- The page is in the admin area for API viewers and administrators. A developer outside the organization gets the addresses from them; the OpenAPI document and the metadata are public on the tenant host.

## Public API and MCP server (spec 035, T020: final checks)

### Built
- `PublicApiPerformanceTests` measures the section 15 targets. Each one stops before the operation runs, so only the overhead is timed. Token validation and authorization use a token without the operation's scope, which covers validation, the app and tenant checks, the limits, the call log and the scope check, then answers 403. The median time of the same request without a token is subtracted, so the in-process transport is not counted. The MCP gateway uses `tools/list`, which covers validation, the external AI client policy and the tool catalogue. The token endpoint uses the client credentials grant, so no identity provider round trip is involved. The MCP and token times include the in-process HTTP pipeline, which makes them an upper bound. The call counts stay under each app's limit for one minute.
- Each public call now reads the API settings once instead of twice, and writes its call log entry, the app and consent last use and the month's count in one statement instead of up to four. This brought the overhead under the target.
- The API access e2e journey, in Arabic and English with axe, now also covers the app's event subscriptions section and the developer page.

### Dependencies
- SIEM stream fields: public API audit entries carry the actor type `app` and the app ID. Call log rows carry the app, the person, the operation, the status, the error class, the latency and the correlation ID. Spec 026 US4 (SIEM streaming, R2) is not built yet. It will stream these entries as they are, so spec 035 needs no new fields, and the stream itself is tracked under spec 026 US4.

## Security and compliance (spec 026 R2, slice 1: T018 to T020 SIEM streaming)

### Built
- `IAuditStream` in `Audit.Contracts` reads the tenant's sealed audit events in chain order. The audit sealer gives each event a gapless position within 5 seconds, so a reader keeps its place with a number and never reads audit tables.
- SIEM destinations (`/api/v1/security/siem`; list for `security.view` or `security.manage`; changes need `security.manage`, with step-up to create, change, delete or turn off; capability `security.siem`, Workplace Plus and above). At most 2 per tenant.
- Two transports. HTTPS JSON posts `{"batch", "events"}`, signed like webhooks with a secret from the tenant's secret manager. Syslog over TLS (RFC 5425 octet counting, RFC 5424 messages, facility log audit) uses TLS 1.2 or later, validates the server certificate and applies the same private address guard as connector HTTP calls.
- Both go through Integrations as an unlisted `siem` pack (`ISiemDelivery`), like the event endpoints of spec 035. So the host allowlist, run history and circuit breaker apply, and the destination is hidden from the Connections page.
- `SiemStreamer` runs every 10 seconds (`SiemStreamWorker` in the workers host, or in the development host with `Security:Siem:InProcess`). It sends up to 20 batches of 500 events per destination per run.
- A failed batch keeps the destination's place and waits with backoff (10 seconds doubling to 5 minutes). The failure is audited as `security.siem_delivery_failed` at most once an hour. After 24 hours the alert `siem_failing` goes to the security officers.
- Nothing is copied into a buffer: the audit stream keeps at least 365 days, which covers the 7 day buffering of US4 scenario 2.
- Test event (`:test`) outside the stream. Configuration changes are audited as `security.siem_configured` with before and after values and the secret reference, never the secret.
- The SIEM tab on the security pages. The schema is in `docs/security/siem-schema.md`.
- Tests: `SecuritySiemTests` (6) and unit `TlsSyslogSenderTests` (3).

### Smallest safe defaults
- A new destination starts at the end of what is sealed when it is made. Events from the last few seconds before that are therefore sent too. Up to 7 days of history can be asked for.
- The stream is the audit stream. Spec 035 call log rows (one per API call, reads included) are usage telemetry and are not streamed. The API's refusals and writes are audited, so they are in the stream.
- Delivery is at least once. A batch whose answer was lost is sent again under a new batch ID only if more events joined it, and receivers drop repeats by event ID.
- The development host does not run the audit sealer, so nothing streams there unless the workers host runs too.

## Security and compliance (spec 026 R2, slice 2: T021 to T023 guests and access reviews)

### Built
- Guest domain rules on the security policy page (`guestAllowDomains`, `guestBlockDomains`). Spaces checks them through `ISecurityPolicy.GuestDomainRefusal` before an invitation goes out. Changes are audited with the policy like every other setting.
- `security.max_guest_spaces` (Workplace 0, Workplace Plus 5, Enterprise and Government no limit) counts active spaces with guests or open invitations. A space's first invitation is checked, under a per tenant lock.
- Guest PDF downloads carry the guest's address and the time on every page, through `IPdfStamp` in `Integrations.Contracts` (the payslip stamp of spec 019, now shared). Every guest download is audited as `doc.downloaded`.
- The reduced guest shell: for a guest, `/api/v1/experience/shell` sends only the tenant's name, notifications, settings and breadcrumbs, with `guest: true`. The home then lists the guest's spaces from `/api/v1/spaces/as-guest`, each with its sponsor (the person who invited them) and end date. The shell labels the person as a guest.
- Access reviews (`AccessReviews`, `AccessReviewWorker` hourly in the workers host, or in the development host with `Security:AccessReviews:InProcess`). Guests come from Spaces through `ISpaceGuestReview`; administrator assignments from Authorization through `AdminAssignmentsAsync`; managers from `IReportingLine`.
- Reviewers decide on `/access-reviews` (`/api/v1/access-reviews`); security administrators see the reviews and open one on the new Access reviews tab (`/api/v1/security/access-reviews`, step-up to open). Reviewers are told by a security notice.
- Audit: `guest.reviewed`, `security.access_review_started`, `security.access_review_decided`, `security.access_review_completed`, and `spaces.guest_removed` from Spaces.
- Tests: `SecurityGuestTests` (4), `SecurityAccessReviewTests` (2), e2e `guests.spec.ts` in English and Arabic with axe.

### Smallest safe defaults
- A domain entry covers its subdomains. Blocked wins over allowed; an empty allowed list allows every domain that is not blocked.
- Guests download PDFs up to 50 MB; a larger PDF, or one that cannot carry the stamp (for example one with a password), is refused to guests. Other file types download unchanged. A per space switch that turns guest downloads off is not built.
- An administrator role is a role holding any permission apps may never hold (`Permissions.Administers`: access, identity, security, the API, audit export and AI policy). Only direct user assignments at tenant or organization scope are reviewed; group assignments come from the directory and are reviewed there.
- The reviewer is the guest's sponsor or the administrator's manager. When that person is gone, is a guest, or is the person reviewed, the first active holder of `security.manage` other than the person reviewed reviews instead. An item with no possible reviewer is left out of the review.
- The first review of a tenant opens 90 days after the schedule first runs for it; security administrators can open one earlier. One review is open at a time.
- An item not decided in 14 days is removed, except the tenant's last tenant administrator assignment, which is kept and recorded as `kept`.
- Removing a guest works like the end of access: the owners are told and a guest left in no space is disabled and signed out.

## Security and compliance (spec 026 R2, slice 3: T024 and T025 alerts in full)

### Built
- Guest access to Confidential items (US5 scenario 3, `security.alerts_advanced`): Documents publishes `GuestOpenedClassified` when a guest downloads or previews a Confidential item, with the space that holds it. Security raises `guest_confidential_access` once per guest, item and day. The tenant setting `guestConfidentialNotify` (security, sponsor or both) decides who is told; the sponsor is found through `ISpaceGuestReview.SponsorOfAsync` and gets `security.guest_confidential.sponsor` with a link to the space. The alert is kept on the alerts page whatever the setting.
- App alerts (US5 scenario 5, `security.alerts`): the PublicApi call log counts refused calls (`error_class = denied`) per app over the last 10 minutes on each refused call only, and publishes `ApiCallsDenied` when the count reaches the tenant threshold. The hourly maintenance run compares the last full hour with the app's 30 day hourly average and publishes `ApiVolumeSpike`. Both carry the app name and the person with the most calls. Security raises `api_denied_burst` (one per app and 10 minutes) and `api_volume_spike` (one per app and hour).
- API administrators (`api.manage`) are told about the three app alert kinds with their own text and a link to the apps page; security responders get the security text. Someone who holds both gets only the security one.
- Thresholds: `GET` and `PUT /api/v1/security/alert-settings`. Reading needs `security.view` or `security.manage` with `security.alerts`; changing needs `security.manage`, `security.alerts_advanced` and step-up, and is audited as `security.alert_settings_changed` with before and after values. PublicApi reads the app thresholds through the `ISecurityAlertThresholds` contract. The form sits under the alert list and is read only when the person or plan cannot change it.

### Smallest safe defaults
- Ranges: download files 10 to 100,000; download data 10 MB to 1 TB; working hours HH:mm with the start before the end; refused calls 5 to 10,000; volume factor 2 to 100, kept to one decimal.
- An hour with fewer than 100 calls, or an app with no calls in the 30 days before, never counts as a volume spike, so quiet and new apps do not raise alerts.
- Stored thresholds keep applying after a move to a smaller package, so a downgrade never makes alerts noisier; they can be changed again only on a package with advanced alerts.
- "Confidential" means the Confidential classification only. Guests never open Restricted items (FR-011), so there is nothing to alert on there.
- A preview counts as opening the item, the same as a download.

## Security and compliance (spec 026 R2, slice 4: T026 and T027 sensitive data detection)

### Built
- `ISensitiveDataScanner.GuardAsync` in Security.Contracts. Content calls it on publish and on submit for approval, with each language's title, summary and body. Documents calls it on upload, working copy and check-in, with the text of the stored file. The result names the type, the tenant pattern name for custom ones, the location (`title.en`, `body.ar`, `file`) and the count, never the matched value.
- Built-in patterns, each checked beyond its shape: Saudi ID and Iqama (10 digits starting 1 or 2, Luhn), Emirates ID (784 and 15 digits, Luhn), Egyptian ID (century, a real birth date, governorate code), Qatar ID (11 digits starting 2 or 3), IBAN (mod 97), payment card (13 to 19 digits starting 2 to 6, Luhn), passport (letter and digit forms next to the word passport or جواز, warn only). Overlapping stretches count once, for the most specific type.
- Tenant patterns: regular expressions with a 100 ms match limit, at most 50, names unique. A pattern is turned on only when its sample text matched; changing the expression needs a new test. Samples are never stored or logged.
- Settings on the security policy row: `detection_enabled` and a mode per location type (`guest_space`, `everyone`, `other`). Changes need `security.manage`, `security.data_detection` and step-up. Patterns are managed with `security.respond` or `security.manage`. Every change is audited with before and after values (`security.detection_changed`, `security.detection_pattern_changed`).
- Location types: a document library in a space with guests or open guest invitations is `guest_space`; a library open to the whole tenant, and content with the audience "everyone", is `everyone`; everything else is `other`.
- Answering a warning: remove the data, raise the classification to Confidential or Restricted, or confirm with a reason (audited as `security.sensitive_data_confirmed` with types, locations and counts). A block is only cleared by removing the data. The editor and the upload dialog show the findings and ask for the reason; the admin page is Security, Sensitive data.
- Documents now keep the text of plain text, Office Open XML and OpenDocument files in `text_extract`, so search finds words inside those files too.

### Smallest safe defaults
- Detection is off until the tenant turns it on, and every location type starts at warn. Block is a choice the tenant makes.
- Raising the classification answers a warning but never lifts a block.
- PDF, images and old binary Office formats give no text, so they are not checked. Text is read up to 1,000,000 characters and 50 MB of XML per file.
- A passport number counts only near the word passport, since the bare form matches many product codes.
- AI classification suggestions (section 11) are not built in R2; detection is pattern matching only, as the plan says.
- The public API check-in takes no reason; a warning there comes back as a conflict with the findings, and the author confirms in the app.

## Security and compliance (spec 026 R2, slice 5: T028 and T029 compliance projects)

### Built
- A new `Compliance` module (schema `compliance`), added to the module map. It owns tenant frameworks, projects, project controls, evidence tasks and evidence. Capability `compliance.projects` (Enterprise and Government only); permissions `compliance.manage` and `compliance.view`, held by the compliance officer role and by tenant administrators.
- Control library: ISO/IEC 27001:2022 Annex A (93 controls), SOC 2 trust services criteria 2017 (CC1.1 to CC9.2, A1.1 to A1.3, C1.1 to C1.2, 38 in all) and NCA ECC-1:2018 (29 subdomains), as versioned JSON in the module with short titles in English and Arabic. Tenants add up to 20 frameworks of their own (key `tenant.<slug>`, up to 500 controls), changed with `compliance.manage` for the whole tenant.
- A project picks a framework and a scope (the whole tenant or one unit with its subtree) and copies the controls, so a later library change never changes a running project. Each control has an owner, a status (not started, in progress, evidence ready, reviewed), a due date, a note, up to 20 linked policies and 20 linked documents, and up to 30 evidence tasks.
- Evidence (FR-023): an audit export is attached to a task. The file is copied into the tenant's storage under `compliance/evidence/`, and the evidence keeps the export ID, format, row count, size and SHA-256. The download sends the hash in `X-Content-SHA256`. The task is then done.
- Readiness: controls by status, controls past their due date, overdue evidence tasks, and the counts by owner and by the owner's primary unit.
- Sharing (FR-024): a manager shares a project to a space they own. Every reader of that space, guests included, reads the project, its controls, tasks, readiness and evidence files, and changes nothing. Unsharing ends that access at once.
- New contracts: `IAuditExports` (Audit.Contracts) lists and opens completed exports and needs `audit.export`; `IPolicyReferences` (Policies.Contracts) and `ISpaceAccess` (Spaces.Contracts) answer as the current user.
- Audit actions: `compliance.project_created`, `compliance.control_status_changed`, `compliance.evidence_attached` (spec), plus `compliance.framework_changed`, `compliance.project_changed`, `compliance.project_shared` and `compliance.control_changed`.
- Pages: Compliance projects (`/compliance`) with the project list and the start form, the control library, a project page with readiness, controls and settings, and a control page with its links and evidence tasks. A space page lists the projects shared to it.

### Smallest safe defaults
- Who changes what: managers (`compliance.manage` over the project's scope) change everything. A control's owner moves its status up to evidence ready, writes the note and adds tasks. A task's owner marks it done and attaches evidence. Only managers mark a control reviewed or reopen a reviewed one.
- Attaching an audit export needs `audit.export`, the same right as downloading it, so no one gets an export through a project they could not have downloaded. Compliance officers do not get `audit.export` by default; a security officer or administrator attaches the export, or the tenant gives the officer that right.
- Owners must be active employees or contractors; guests only ever read.
- Linked policies and documents are checked as the editor when added, and shown as each viewer may see them: one the viewer cannot open shows as unavailable, without its name.
- Projects and evidence are kept 7 years (section 7): they are archived, never deleted. An archived project is read-only. A task with evidence cannot be removed.
- Sharing needs ownership of an active space; there is no step-up, but every change is audited. Evidence downloads are not audited separately.
- Overdue means a due date before today in UTC. An export file over 200 MB is refused as evidence.

## Security and compliance (spec 026 R2, slice 6: T030 hosting region and break-glass)

### Built
- Hosting region (FR-008): an installation serves the regions in `Platform:Regions` (`Offered`, and `Default`). Tenant creation takes an optional `region`; none means the default. A region the installation does not host is refused, and a region other than the default needs the new capability `deployment.region_choice` (off on Workplace, on for the rest), checked after the package is assigned so a refusal rolls the whole creation back. The region is stored in `tenancy.tenant_settings.region`, read through `ITenantSettingsReader.GetRegionAsync`, listed for operators at `GET /api/v1/platform/regions`, and shown on the security posture page.
- Break-glass (FR-020, spec 001): platform operators open a session on one tenant at `POST /api/v1/platform/tenants/{id}/break-glass` with a reason (10 to 500 characters) and a length (15 to 240 minutes, default 60), list sessions, end one, and while it is open reset the tenant's network allow lists (`network-rules:reset`), the recovery the spec names for a lockout. Sessions live in `security.break_glass_session` under the tenant's row-level security, written with the subject tenant in context.
- Opening, each action and the end are written to the tenant's own audit stream, so they show on the tenant's audit page and go to its SIEM, and to the platform stream with the tenant as subject. Audit actions: `platform.break_glass_opened`, `platform.break_glass_network_reset`, `platform.break_glass_ended`.
- Opening raises a `platform_access` security alert, so security officers are notified at once. The posture page gains the check "No platform operator access open", which fails while a session is open and links to the audit page filtered to the openings (`/admin/audit?action=...`).

### Smallest safe defaults
- Default installation: one region, `primary`. Each region is a separate deployment with its own database, object storage, backups and search index; a tenant is only created in an installation that hosts its region, so its data never leaves it. Moving a tenant between regions is not offered.
- Tenants created before regions read as the default region.
- Break-glass gives one action for now, the network rule reset; reading tenant content is not offered. The reset clears the allow lists for all pages and for administration pages and keeps the block list; the audit entry holds the values before, so the tenant can restore them.
- One open session per operator per tenant. A session ends when the operator ends it or when it expires; expiry needs no job, since every action checks the expiry. A closed tenant cannot be opened.
- The reason the operator types is shown to the tenant in the alert and the audit entry, so operators are told not to put customer data in it.

## Process engine (spec 024 R2, slice 1: T009 to T017)

### Built
- FEEL subset (FR-005) in `Workflow/Feel`: a hand-written lexer, parser and interpreter with no reflection and only the listed functions, unary tests for decision cells, and limits of 4,000 characters, 64 levels, 20,000 steps, 50 ms, 10,000 list items and 256 KB per result.
- Process models (FR-001 to FR-003): `workflow.process_model` holds one draft per key with its owner unit, names per locale and the problems the validator found. `PUT /api/v1/workflow/processes/{key}` saves a draft even with problems; `POST /api/v1/workflow/processes/{key}:publish` refuses while there are any, with one validation error per element (`elements.<id>`), and otherwise stores the next immutable version of `process:<key>` in `workflow.process_definition` with `kind = 'graph'`. Publishing the same model again keeps the version. The BPMN of any version is at `GET .../versions/{n}/bpmn`.
- The BPMN reader takes the R2 subset with the TeamBeats extension namespace (names per locale, assignment, due, connector or webhook, input and output mappings, decision, correlation, loop). XML is read with DTDs prohibited, no resolver and a 2 MB limit; at most 500 elements.
- The token executor (`Graph/Executor.cs`) moves tokens in memory and returns effects; `ProcessEngine.Graph.cs` applies them in the caller's transaction with the instance row locked and saves tokens, scopes and visit counts in `process_instance.state`. Signals to other instances (a child's end, a message from a send task, a child start) run after the state is saved.
- User tasks of process models land in `workflow.user_task` with their token and show in the spec 016 inbox through `ProcessModelTasks` (definition keys starting `process:`), titled with the step's names per locale. A completed task writes `tasks.<element>` and `outcome` to the variables.
- Timers: dates, durations, cycles and working days. Token timers are `engine_job` rows of kind `timer`; timer start events are rows of kind `timer_start` registered at publish for the current version only. A missed timer fires once; a cycle schedules its next run from the time it fired.
- Messages: `ProcessEngine.CorrelateAsync(name, key, messageId, payload, source)` correlates within the tenant, once per message ID (`workflow.message_buffer`). The oldest waiting token takes it; otherwise a message start event starts an instance; otherwise it waits in the buffer for one hour and the next token to subscribe takes it.
- Call activities start the current version of `process:<called key>` as a child (business key `call:<parent>:<token>`), at most 10 deep, and pass the child's variables back through the output mappings (`result.<name>`). Cancelling or withdrawing a parent cancels its children.
- Incidents (FR-011, FR-013): a step that cannot go on (an expression error, nobody to assign, a timer that cannot be read, a loop over 1,000 passes, a called process that is missing, a technical failure of a connector action) raises an incident on its token. `ResolveIncidentAsync` retries, skips or cancels it as for routes.
- Signed webhooks as messages (FR-009): the inbound webhooks of spec 017 US5 already check the signature (with the previous secret during its overlap), the time window and the delivery ID, and map a delivery to a process message with a correlation path and declared fields. Workflow handles `InboundWebhookReceived` with target `message` by correlating it with the message ID `webhook:<connection>:<delivery>` and the declared fields as the payload, so a redelivery correlates once. No second webhook endpoint was added.
- Webhook steps (FR-010): a service task with `tb:webhook id` calls the `deliver` action of that webhook connection with `{ type: "process.step", occurredAt, data: <inputs> }`, so it is signed, sent only to allowlisted hosts, retried and logged by the connector runtime like any other connector call, and moves on when the delivery succeeds.
- Decision tables (FR-007): `workflow.decision_model` holds one DMN draft per key with its owner unit; `PUT`, `:publish` and `:test` under `/api/v1/workflow/decisions/{key}` need `decision.design` at the owner unit. Published versions in `workflow.decision_definition` keep the compiled table; the same table again keeps its version. Every hit policy is supported (UNIQUE, FIRST, PRIORITY, ANY, COLLECT with SUM, MIN, MAX and COUNT, RULE ORDER, OUTPUT ORDER). UNIQUE tables are checked for overlapping rules where every cell is a comparable literal, range or list; other overlaps fail at run time with `unique_violated`. Business rule tasks evaluate the current version when reached, write the result to the named variable and record the matched rules in the history.
- Operator view (FR-013, US5): `GET /api/v1/workflow/instances` searches process model instances by process, version, state, business key, starter, unit, start date and open incident, newest first, keeping those at units where the caller holds `process.operate`. `GET .../instances/{id}` shows the variables, the elements waiting now, open tasks, incidents and history. `PUT .../instances/{id}/variables` (only while an incident is open), `POST .../instances/{id}:cancel` and `POST .../incidents/{id}:resolve` (retried, skipped or cancelled) each need a reason and write an audit event; a second resolve of the same incident is refused with 409.
- Monitoring (FR-014): `GET /api/v1/workflow/processes/{key}/monitoring?version&from&to` (default the latest version and the last 30 days, at most a year) gives active, completed, cancelled and incident counts, the average and 90th percentile duration of finished instances, and per element the tokens waiting now, open incidents and the average and 90th percentile time of user and service tasks finished in the period, with the five slowest elements. It needs `process.analyze` or `process.operate` at the model's owner unit and holds no variables or people. Each ended instance is counted for spec 021 as the unkeyed analytics event `process.instance_ended` (process, version, completed or cancelled, seconds).
- Pages (T016): Processes (`/admin/processes`), Decisions (`/admin/decisions`) and Process operations (`/admin/process-operations`, with a Monitoring tab) in the content group of administration, each shown only when the navigation lists it (`workflow.bpmn` and the page's permission). Validation problems come back already worded in the reader's language (`problems[].message`), so pages do not keep a second copy of the texts.
- The modeler draws on React Flow (`@xyflow/react` 12.12.0, MIT, as ADR-0017 chose). It offers the 16 kinds of the subset in the palette, a properties panel per kind, and works from the keyboard: the palette adds after the selected element and connects to it, the panel connects, edits and removes, the arrow keys move a focused shape and the outline lists the model in order from the start. The canvas stays left to right in Arabic; panels follow the page direction. A model with elements the canvas cannot draw (such as sub-processes) opens as XML, which can be saved as it is.
- Correlation speed and worker failure (T017): `ProcessEnginePerformanceTests` correlates 100 messages by key with 300 instances waiting, each in its own transaction, under 1 second at p95 (section 15); and a worker that dies while it holds a due timer leaves nothing behind (its transaction rolls back), keeps other workers off the row while it lives (`for update skip locked`), and the next worker runs the timer once.
- Test run (US1 scenario 4): `POST /api/v1/workflow/processes/{key}:test` walks the saved draft with the engine's own executor in memory (`ProcessSandbox`), as an owner with `process.design`. It writes nothing: no instance, task, job, subscription, message, connector call or audit. User tasks show who they would go to (named users, the sample requester's line manager or manager level, the assignee expression, the permission, or the fallback) and end with the outcome the designer picks (approve by default); intermediate timers are skipped ahead; boundary events and event gateway targets fire only when the designer names them; service tasks, webhooks, called processes and messages are pretended with the results the designer gives (empty by default); decisions use their current published version and show the matched rules. A run stops after 200 actions. A draft with problems answers with them and no steps. The modeler shows the steps in order and tints the shapes the run passed.
- Permissions `process.operate`, `process.analyze` and `decision.design` with the built-in roles Process operator and Process analyst; Workflow designers also hold `decision.design`. Capabilities `workflow.bpmn` (off on Workplace) and `workflow.max_active_instances` (10,000 on Workplace Plus, no limit on Enterprise).

### Smallest safe defaults
- Starting a process model instance by hand (`POST .../instances`) needs `process.operate` at the owner unit and at the chosen unit; other modules start them through `IProcessEngine.StartAsync` with their own checks.
- A process model does not exclude its starter from its tasks unless the caller names people to exclude; approval routes keep excluding the starter.
- A task whose assignment resolves to nobody falls back to its fallback users, and without them raises an incident `no_assignee`.
- A business error from a connector action that no error boundary or error event sub-process catches is an incident, not an end of the instance.
- Graph user tasks take the inbox outcomes approve, reject and return; models branch on `outcome`.
- Messages with no correlation key match subscriptions that have none.
- The operator view covers process model instances only; approval routes of requests keep their incident actions on the request (spec 016).
- Variable classification is not yet set per variable in models, so operators see every variable and analysts see none; masking Restricted variables comes with the modeler's variable settings.
- Process model keys are at most 64 characters so that the key is a valid analytics key.
- A webhook step has no outputs, since a delivery returns no body; models that need an answer use a connector action or wait for a message.
- Product events as message and start triggers are built with spec 016 R2 triggers, which lists them; connectors send messages through the same `CorrelateAsync` with source `connector`.
- A decision test runs up to 100 rows against the draft and reports each row's result, matched rules or error code.
- Deviation from ADR-0017's list: the modeler reads and writes BPMN and DMN with the browser's own `DOMParser` and `XMLSerializer` (`lib/bpmn.ts`, `lib/dmn.ts`) instead of `bpmn-moddle` and `dmn-moddle`, and places shapes without diagram positions in columns by distance from the start instead of `@dagrejs/dagre`. The subset is small and the server reads the XML again, so three more packages were not needed; they remain allowed if import from other tools (R3) needs them.
- Process analysts can open a model read only, as they can list it; saving and publishing still need `process.design`.

## Service catalogue and workflows (spec 016 R2, slice 1: T018 to T020 step graph routes)

### Built
- Group and queue candidates (FR-006): `tb:assignment groups` names groups (product or directory) whose members may complete the task, besides users, a permission at a unit, the line manager and the manager level. Membership is read when the task is checked, so a member who joins or leaves a queue gains or loses its open tasks at once. People the instance excludes (the requester) stay out even when they are members.
- Reminders and escalation (US5 scenario 2): `tb:due workingDays` keeps reminding the candidates at the due time (`UserTaskDue`); `tb:escalation afterWorkingDays users groups` hands the task, that many working days after the due time, also to the target. The task keeps its candidates, the history records `task_escalated`, and `UserTaskEscalated` names the people it now reaches so the owning module can tell them. A calendar change moves the due time and the escalation together.
- Product actions: a service task with `tb:product action` runs inside the product. `set` writes its inputs to variables (directly, or through its outputs as `result`); `notify` and `start_service` publish `ProcessActionRequested` (unique per step run) for the module that owns the process, and the step moves on at once. Test runs carry out `set` and show the other two as not carried out.
- End results: `tb:result value` on a plain or terminate end event is the instance's result, kept in `process_instance.result` and sent on `ProcessInstanceCompleted.Result`, so the owner learns whether a request was approved or rejected.
- Modules publish compiled models through `IProcessEngine.ValidateModelAsync` and `PublishModelAsync(key, source, bpmn, owner)`, which run the reader, the validator and the reference checks (groups that exist in the tenant, permissions, connector actions with their declared inputs, called processes). Keys of process models (`process:`) are refused there. Only process models need `workflow.bpmn` and count against `workflow.max_active_instances`; compiled routes are gated by their module.
- Step graph routes (T019): a service route is either the R1 shape or `route.graph`, a list of steps (approval, task, set field, notify, connector, webhook, wait, condition, split, join, start service, end) and the links between them, with names per locale. Saving a draft checks the shape (known types with their settings, one first step, ends lead nowhere, every other step leads on, conditions only out of a condition or split and only on form fields) and demands the capabilities the steps need: `workflow.advanced` for conditions, splits, joins and escalation, `workflow.connector_fulfilment` (new, Workplace Plus and above) for connector and webhook steps, `workflow.automation` for set field, notify, wait and start service. Approval and task steps in sequence work on Workplace.
- `RouteGraphCompiler` turns the graph into BPMN with the step IDs as element IDs. An approval is a user task with a decision after it: approve goes on, reject and return end the whole route (a terminate end with that result); a task step goes on when done and ends the route as rejected when rejected with a reason. Conditions compare a form field with a constant; a split with conditions becomes an inclusive gateway and its join waits likewise. Publishing sends the compiled model to the engine as the next version of the service's key; problems come back on the step they belong to (`route.graph.steps[i]`), so a split with no join or a step that cannot be reached blocks publishing (US8 scenario 7), and open requests finish on their version (US5 scenario 3).
- Requests on a step graph route start in approval with the form values as `form`, `request.number` and `requester.name|email`, and with the filer and the person it is for kept off every step. The inbox and the request show the open step's names; the engine's candidate check alone decides who acts (a queue member needs no approver role). Each decision is recorded under its step (`approved`, `rejected`, `returned`, `task_done`, `task_rejected`); when the route ends the request takes the result of the end it reached and, when approved or completed, Tracking lists the steps never reached as `skipped` (US5 scenario 1).
- Notifications: the people a person step names (users, the line manager or manager at a level, members of its groups) are told it waits, reminded at its due time and, for an escalation, the people it now reaches are told; a notify step tells the requester, the person it is for, named users and group members with the request number and link.

### Smallest safe defaults
- Product actions are refused in process models designers write (`validation.process.product_action`), because only the module that owns a process carries them out; they come from compiled service routes.
- An escalation needs a due time on the same step and someone to go to; the escalation target is named users and groups. Escalating to the manager of a candidate waits for a spec rule on whose manager that is.
- A group with no members still counts as an assignee, as a permission does: the task waits until someone joins the queue, and does not raise `no_assignee`.
- A notify step sends the product's own "update on request" message with the request number and link; owner-written message text waits for per-locale notification texts in spec 015.
- A wait step waits a number of working days on the request unit's calendar; waiting until a date in a form field or for a named event comes with the triggers slice.
- A start service step hands the start to Services and moves on; slice 2 (T021) starts the other service's request.
- A business error from a connector or webhook step of a step graph route stops the request at that step ("automation failed") for an owner to retry, skip or cancel; mapping business errors to paths waits for the designer (T024).
- A set field step writes a route variable that later conditions and inputs cannot yet read, since conditions use form fields only; it is there for connector inputs and analytics once the designer offers variables.
- R1 routes (one approval and connector steps) keep running in their own shape; the designer (T024) opens one as a graph and saves it as a graph, which is how they convert.
- Parallel tasks: when one is rejected the route ends as rejected and the other open tasks close.

## Service catalogue and workflows (spec 016 R2, slice 2: T021 triggers)

### Built
- Start triggers (FR-014) live on the route of a service version as `route.triggers` (at most 10): a schedule (`weekly` on day 1 Monday to 7 Sunday, `monthly` or `quarterly` on day 1 to 28 of the month or the quarter's first month) with its system requester, or a product event: `user_provisioned`, `user_deprovisioned`, `membership_changed` (optionally only for one unit) and `request_completed` of another service (optionally only for one end status). Triggers need `workflow.automation` on save, publish and every run.
- A trigger starts a request of the current published version for the person the event is about (for a schedule, the system requester), filed by the trigger's system requester (`requesterId`), so it shows in the person's list, their manager's team list and the system requester's list (US8 scenario 2). The system requester stays off the request's steps, as any filer does. A start service step files the other request as the source request's filer did, for the same person. Tracking starts with `triggered`, and the audit records `request.started_by_trigger` with the trigger and event.
- Idempotent per event: each request keeps its event key (`user_provisioned:{user}`, `membership_changed:{user}:{unit}:{change}`, `request_completed:{request}`, `action:{step run}`, `schedule:{frequency}:{day}:{period}`), unique per service, so a repeated event starts nothing more.
- Start service steps start the other service's request for the same person and record `service_started` with its number on the source request. Requests started by requests (steps or `request_completed` triggers) carry their chain depth; past depth 5 nothing starts, the request that would have gone deeper records `chain_stopped`, and the audit records `request.chain_stopped`. A service never starts itself, by a step or by a trigger on its own completion (`validation.services.route_cycle`); longer loops stop at run time.
- Requester leaves: on `UserDeprovisioned` the open requests for that person (filed by them, or for them by someone else) (submitted, in approval, returned) are cancelled with `requester_left` on Tracking and their route withdrawn, except on versions marked `continueAfterExit` and the requests that leaving itself started.
- `ServiceScheduleWorker` runs the schedules every 15 minutes (workers host, or the development host with `Services:Schedules:InProcess`).
- Tests: `ServiceTriggerTests` (6).

### Smallest safe defaults
- A triggered request carries no form values: its steps read the request and the requester. Prefilling from the profile and HR runs as the person signed in and is not done for the system actor.
- Schedules run on the system requester's working calendar and start on or after their day in each period. A period whose day came before the version was published is not caught up, so publishing never starts a request for the past.
- The system requester is an active person of the tenant named on the trigger: required on a schedule, optional on an event trigger, where the person the event is about files it when none is named. A request filed for someone else whose filer leaves keeps running; it is cancelled when the person it is for leaves. A service owner alert for a stopped chain waits for owner notifications (spec 015); the alert is on the request's Tracking and in the audit log.
- Approved requests whose connector steps still run are not cancelled when the requester leaves; only requests that could still be cancelled by the requester are.

## Service catalogue and workflows (spec 016 R2, slice 3: T022 service levels and auto-approve)

### Built
- Auto-approve rules (FR-015, US5 scenario 4): an approval step of a step graph route may carry `autoApprove`, all of up to 10 comparisons of form fields with constants. The compiler enters the step through a check: when the rule holds, a product action `auto_approve` completes the step as the system and the route goes on past it; otherwise the step waits for its assignee. Both ways out meet in a merge, so a join after the step counts one path. Services records `auto_approved` under the step on Tracking and audits `request.auto_approved` with the rule. Rules need `workflow.automation`.
- On a Confidential or Restricted service, saving or publishing a route with an auto-approve rule needs `tenant.settings_manage` tenant-wide (the tenant administrator's sign-off of section 15).
- Service level targets: `route.serviceLevelDays` (1 to 90 working days) on the version. A request submitted or started by a trigger gets `due_at` (that many working days later on its unit's calendar, at the same time of day) and `at_risk_at` (75% of the way). `ServiceLevels` runs with the schedules every 5 minutes: an open request (submitted, in approval, returned) past `at_risk_at` records `sla_at_risk`, past `due_at` records `sla_breached`, each once. Both alert the people its open steps wait for and the owners (`approval_escalation` category); a breach is also counted for spec 021 as `request.sla_breached` (unkeyed, per service) and audited.
- The request shows its service level: due time and on track, at risk, breached, or met when it closed in time.
- Tests: `ServiceLevelTests` (4).

### Smallest safe defaults
- The target sits on the route of the version (versioned with it) rather than a separate version column, so it travels with the route the designer saves.
- The clock runs while a request is returned to the requester; pausing it waits for a spec rule.
- Owners alerted are the people holding `service.manage` tenant-wide who hold it at the owning unit; owners scoped to a unit only see it on the request and in analytics until the authorization service can list holders at a unit.
- Test runs of a draft show an auto-approve step as not carried out; the designer's test mode (T024) shows the path the rule picks.

## Service catalogue and workflows (spec 016 R2, slice 4: T023 fulfilment queue)

### Built
- Picking queue tasks (US6 scenario 1): the engine keeps `claimed_by` on a user task (`ClaimTaskAsync`, `ReleaseTaskAsync`, history `task_claimed` and `task_released`). While the picker is still a candidate, the task is theirs alone (or their delegate's, acting for them): the rest of the queue no longer sees it, cannot pick it and cannot decide it. A pick lapses when the picker stops being a candidate, for example when they leave the queue group.
- `POST` and `DELETE /api/v1/tasks/{id}/pick`. The inbox marks queue steps (a step assigned to groups) as `pickable` with `pickedBy`, and offers Pick and Put back. Tracking records `picked` and `put_back` under the step. Rejecting still needs a reason.
- A failed automatic step (an incident on a connector or webhook step) records `automation_failed` and alerts the service's fulfilment queue (`fulfilmentQueueGroupId`) and its owners, once per incident (US6 scenario 3, US8 scenario 5). Retry, skip and cancel stay with the owners (spec 017 T009).
- Tests: `ServiceQueueTests` (3).

### Smallest safe defaults
- Picking is offered on steps assigned to groups. Any other step can be picked through the API by a candidate, which only matters when several people may act on it.
- An R1 route with a fulfilment queue still ends after its approval and connector steps; the designer (T024) opens it as a step graph where the queue becomes a task step.

## Service catalogue and workflows (spec 016 R2, slice 5: T024 route designer)

### Built
- Test mode (US8 scenario 6): `POST /api/v1/services/admin/{id}/test` compiles the latest draft's step graph and walks it in the spec 024 sandbox through the Workflow contract `IModelTester`, as a sample employee (the owner when none is named), with sample form values and the outcome picked per person step. Nothing is created or sent. The result lists each step reached with the resolved assignees, the paths taken (by link) and auto-approvals, or the problems on their steps. Owners only (`service.manage` at the owning unit).
- `GET /api/v1/services/admin/groups` lists the tenant's groups with member counts for service owners, through the new Authorization contract `IAuthorizationProvisioning.GroupsAsync`. The designer uses it for group assignees, escalation and notify targets, and the builder for the fulfilment queue, which the draft form now saves.
- The route designer in the service builder, on React Flow like the process modeler (ADR-0017): a palette of the twelve step types, the canvas, a settings panel per step (assignee with due time and escalation, auto-approve conditions, connector action with input mapping, webhook, set a value, notify, wait, start another service, end result), paths with conditions added from the panel, and a text outline in reading order. Problems the server returns on `route.graph.steps[i]` and `route.graph.links[i]` are shown on their steps. Route settings cover the service level target, continuing after the requester leaves, and the start triggers. The test panel saves the draft first and marks the steps a run reached.
- Tests: `ServiceRouteTestModeTests` (2), e2e `route-designer.spec.ts` in English and Arabic with axe.

### Smallest safe defaults
- Canvas positions are not stored: the designer lays steps out in columns by distance from the first step, and dragging only moves them for the session.
- With no step selected, a new step goes at the start of the route; with a step selected, it goes after it (between the step and its next step when it has one path).
- Opening an R1 route in the designer turns it into approval, connector steps, a task for the fulfilment queue when there is one, then an end as completed. Going back to the simple route drops the graph on the next save, after a confirmation.
- A webhook step takes the outbound webhook's ID as text, as there is no listing of webhooks for routes yet.

## Service catalogue and workflows (spec 016 R2, slice 6: T025 starter templates)

### Built
- The starter template pack of section 12 as versioned product configuration (`src/Modules/Services/Templates/starter-pack.json`, embedded). Each template carries its settings, form and step graph. Twelve templates: business cards, company transportation, device maintenance, purchase request, expense claim, travel request, training request, VPN or system access, resignation, HR appointment, parking waiting list and referral.
- `GET /api/v1/services/admin/templates` and `POST /api/v1/services/admin/templates/{key}` for catalogue owners of the whole tenant (`service.manage` at tenant scope). Switching a template on creates a tenant-owned draft service through the ordinary create and draft-save paths, so it passes every check a hand-built draft does; `services.service.template_key` (migration `services/0006`) records the template, and switching on again returns the same service. The admin services page lists the templates with Switch on, the state of the service made from each, or "not in your plan".
- Tests: `ServiceTemplateTests` (3).

### Smallest safe defaults
- Fulfilment steps in templates go to people holding `service.manage` at the requester's unit, so a draft works before a queue exists; the owner sets a queue group or another assignee before publishing.
- The simple expense claim is not offered when `hr.expenses` is on (section 12). A template whose steps need a capability outside the plan is shown but cannot be switched on.
- The resignation template keeps the 60 day notice as a rule on the last working day, keeps requests going after the requester leaves, and is confidential. The offboarding journey of spec 022 starts from it when an HR journey template names the service, which the HR administrator sets.
- Speak Up is not in the pack: spec 023 builds it as its own channel with the anonymous option. The parking waiting list does not show a queue position yet, and the HR appointment asks for a preferred date and time of day instead of a slot; both are gaps to close with booking (spec 031) if the tenant needs them. Referrals are followed in My requests.

## Service catalogue and workflows (spec 016 R2, slice 6: T026 request summary)

### Built
- `POST /api/v1/requests/{id}/summary` (section 11, R2): someone who may see the request gets a short summary in their language, written through the AI gateway with use case `summarize` and the service's classification. Only what the reader already sees on the request goes in (service, number, status, people, form values with their labels, attachment count and Tracking), passed as data inside the user message after the instructions. Nothing is stored. The request page shows a Summary panel beside the decision, marked as written by AI.
- Tests: `RequestSummaryTests` (2), including the default AI policy refusing a confidential request (outcome `policy_blocked`).

### Smallest safe defaults
- The summary is gated by `ai.assistant`, which every package has, and by being able to see the request; the panel is shown to the person deciding. It is asked for on demand, never generated ahead.
- Agent-requested approvals marked in the inbox (spec 027 US4) depend on agent approver tasks, which the agent runtime still refuses (`tool.approval_not_available`) until spec 027 R2 builds them. The inbox marking is built with that slice, not here.


## Spec 009 R2 slice 1: sites (T025, T026)
- A site's home page is a spec 032 page owned at the site's org unit, reached through the Experience contract `ISitePages`; creating from a site template makes the page with the page builder's own permission for the caller, when the plan has `experience.page_builder`. Linking an existing page needs one owned by the same unit.
- Navigation and theme are not copied into the site: the theme is the spec 033 brand assignment at the site's unit, and the reader's site list (`GET /api/v1/content/sites/mine`, page `/sites`) is the navigation entry. Tenants add `/sites` to the global menu as an IA node when they want it in the header.
- Site templates are product configuration in code (`subsidiary`, `location`, `brand`), each setting the home page template (T05, T04, T06), comments, moderation and whether news and announcements need approval.
- A site owner is anyone holding `content.admin` at the site's unit through a role grant; there is no built-in site owner role. Moving a site to another unit needs `content.admin` at the new unit too. Creating sites stays with tenant-wide `content.admin`.
- A site linked to a unit now defaults its audience to that unit's subtree (US6 scenario 2). Sharing adds an audience that only widens who finds the site in their list.
- Archiving makes the site's items read-only (409 `conflict.content.site_archived` on any change), removes them from the feed and the site from the reader's list. Items stay readable by link with an "archived site" label. Search keeps the items; an archived label in search results waits for a label field in the search contract (spec 012).
- Restoring an archived site counts against `content.max_sites` again. The default site cannot be archived.

## Spec 009 R2 slice 2: mentions, hashtags and follows (T027, T028)
- A mention in a body is an inline run with `mention` set to a user ID. Saving a draft refuses a mention of anyone the author does not see in the directory (`validation.content.mention`), so mentions never reveal people. Mentions in comments are a list of user IDs next to the plain text, with the same check.
- People mentioned in an item hear of it when a version goes live (a `ContentPublished` handler), once per item; people mentioned in a comment hear of it when the comment is visible (at once, or when a moderator approves it). Both only when the person may read the item; the author is never told.
- Hashtags are found in the text of the live version in every locale: a # at the start or after a non-word character, then 2 to 50 letters, digits or underscores in any script, lower cased, at most 10 per variant. They are stored per live item (`content.item_tag`) at go-live.
- The topic page (`GET /api/v1/content/topics/{tag}`, page `/content/topics/{tag}`) is the feed filtered by tag, so it applies the same targeting and read checks and counts only permitted items.
- Follows are per person (`content.follow`), at most 200: a site the person works in or that reaches them, or any topic. `GET /api/v1/content/feed?following=true` lists items of followed sites and topics plus every critical item, since a preference never hides mandatory content. Page `/content/following`.
- Mention suggestions in the comment box use the people directory search, which lists only people the writer may see.

## Spec 009 R2 slice 3: AI translation (T029, T030)
- `POST /api/v1/content/items/{id}/translate {locale}` needs `content.ai_translation` and the right to write the item. It sends the primary variant's texts (title, summary, every text run, alt and link text) as a JSON list through the AI Gateway (use case `translate`, the item's classification, a JSON schema answer) and rebuilds the variant from the answer, so blocks, marks, links and mentions keep their place and the model cannot change the structure. The result is the draft variant for that locale, marked `machineTranslated`.
- Answers: `translated` with the item, `policy_blocked` when the tenant AI policy refuses the classification (Confidential by default), `unavailable` for any other refusal or an answer of the wrong shape. Nothing is written unless the answer is used.
- Nothing marked machine translated can be published or submitted (409 `conflict.content.machine_translated`). The mark goes when a person edits that variant, or when an editor or publisher approves it as it is (`POST .../translations/{locale}/approve`). Both are audited (`content.translated`, `content.translation_approved`).
- Smallest safe default for the "reviewed translation" content type setting of the plan: it is not a setting. Every machine translation needs a person before publishing, whatever the type, which is the stricter reading of section 6.
- The translate button shows for every non-primary language; a plan without the capability answers 403 and the editor shows the message.

## Spec 009 R2 slice 4: multi-step approval (T031, T032)
- A site route for a content type takes `steps`, a list of up to five steps, each with its own `approvers`; the R1 shape (`approvers` for one step) still works and both may not be set together. More than one step needs `content.multistep_approval` (403 without it); one step written as a list is still one step.
- The route compiles to one engine definition with a user task per step (`review`, `review2` and on), run in order. A one step route keeps the R1 shape and hash, so existing definitions are reused. A change of steps takes effect on the next submission.
- Every step drops the item's writers (author, submitter, creator) from its named approvers; a step left with nobody goes to the site's publishers by permission, with the writers still excluded by the engine.
- Each passed step adds `step_approved` to the review history (with the decider and comment); the last approval adds `approved` and releases the version as before. A return or reject at any step ends the review and sends the draft back.
- Named approvers of each step are told when their step opens. The route editor sits on the site settings page (`/content/sites/{id}`), with a person picker per approver and add or remove step.

## Spec 009 R2 slice 5: campaigns (T033, T034)
- A campaign belongs to one site and is run by those who publish there (`content.publish` at the site); others do not find it. It has a name, an audience (the site's default audience unless set) and up to 20 steps, each an item of the site, a time, `mandatory` and `onlyNotActed`. Capability `content.campaigns`.
- The plan changes only while the campaign is a draft; then it is started (409 `conflict.content.campaign_started` on later changes) and can be cancelled until it ends, which cancels the steps not sent. A started campaign completes when no step is waiting.
- The content scheduler sends due steps as the system actor. Recipients are the audience members who may read the item (targeting is not access), less those who already acted when the step asks for it. A step whose item is not live is skipped and shows so.
- Smallest safe default for channels: a step is one spec 015 notification. An optional step uses the `announcement` category, so in-app always and email, Teams or push as each person prefers; a mandatory step uses `critical_announcement`, which ignores preferences and is labelled mandatory. Choosing a channel per step waits for spec 015 to accept a channel restriction on a send.
- "Acted" means confirmed the item when it asks for confirmation, otherwise opened it after a step of this campaign reached them. The campaign link is `/content/read/{item}?c={step}`; the reader view records an open for people a campaign reached, and a click when they came through that step's link. Nothing is recorded for anyone else.
- The dashboard shows per step and in total the audience, delivered (notifications created or grouped), opened, came through the campaign and confirmed. Counts of what people did are null below the minimum group size of 5 (spec 021 US1 scenario 2); the unit and location breakdown merges smaller groups into "other" and leaves "other" out when it is still too small.
- Audience units and groups must exist in the tenant (400 `validation.content.campaign_audience`), so another tenant's IDs are refused rather than matching nobody. The send log keeps recipients for the campaign retention of section 13.

## Spec 009 R2 slice 6: retention, health and signage (T035, T036)
- Retention is a content type setting (`retentionMonths` 1 to 120 with `retentionAction` `versions` or `item`, both or neither) in the content settings of communications administrators, run by the content scheduler. `versions` deletes versions older than the period except the live and draft ones; `item` deletes archived items unchanged for the period with their comments, reactions, events and tags. Each deletion is audited (`content.retention_applied`).
- Never deleted: an item under a legal hold, an item a campaign sends (its send log has its own retention) and an item people confirmed, as confirmations are insert only evidence (FR-007).
- Legal hold starts here as a Documents contract (`ILegalHolds`) with a register of holds on individual resources; every module asks it before deleting. Placing and releasing holds from the compliance screens, and holds by custodian, library, folder or query, come with spec 011 R2.
- Health adds per site whether it has no owner (nobody active holds `content.admin` at its unit or a subtree above it; tenant scope does not count, as communications administrators look after every site) and, for an archived site, how many items are still live by link. This needed `OrgHoldersAsync` on the authorization provisioning contract.
- Signage: a site owner names screens on the site settings page and gets an address `/signage/{token}` shown once. The token is 32 random bytes kept only as a SHA-256 hash; rotating or removing the screen stops the old address. The feed (`GET /api/v1/signage/{token}`, no session, tenant from the host, 120 requests a minute per address) lists up to 20 live items of the site that are Public and for everyone, in the tenant's default language.

## Spec 011 R2 slice 1: retention, disposition and legal hold (T015 to T018)
- New built-in role Records Manager (spec 004 FR-011) with `records.manage` at tenant scope; tenant administrators hold it too. Authorization migration 0036 adds both to existing tenants. Labels and disposition need `records.retention`, holds `records.legal_hold` (Enterprise and Government).
- A label has names, a period of 1 to 1200 months, a trigger and an action (`review`, `delete`, `archive`). Smallest safe defaults for triggers the spec leaves open: `effective` is the effective date of the version in force, `superseded` is when a controlled document was withdrawn (no version left in force; a later version replacing an earlier one does not end the document), `field` is a date field of the document's type. A document whose trigger has not happened is inside its period.
- Labels apply by library, by document type or by hand. With both a library and a type label, the longer period wins (section 6, one label per document); a label set by hand stays until the records manager clears it, after which the document takes its library and type label again. New documents take their label on upload.
- Each document keeps the period and action it was labelled with. Editing a label to a longer period reaches every labelled document at once; a shorter period or another action reaches them only with `reapply` (US6 scenario 5). Retiring a label stops new applications; documents keep it.
- A label inside its period refuses user deletion with 409 `conflict.documents.retention_rule`, the same message a legal hold gives, so a hold does not show (section 14).
- The document maintenance worker acts on ended periods: `review` puts the document in the records manager's queue; `delete` deletes it without review unless it is checked out or met a hold before (then it goes to review); `archive` keeps the document for contributors, read-only (409 `conflict.documents.archived` on check-out), and takes it away from readers and search. Documents in a release wait for the release to end.
- Disposing deletes the document, its versions, tokens, read confirmations, own permissions and files, keeps a disposition record (document ID, number, label names, version count, mode, approver, time; never the name or content) and publishes `DocumentDisposed`, which search handles like a deletion. Keeping sets the period to end the chosen number of months from now.
- A hold names exactly one of: a custodian (documents they created or wrote a version of in an optional date range, and optionally their audit records), a library with an optional folder, or one document. Targets must belong to the tenant (400 otherwise). Holds, their reasons and what they cover are shown only to records managers; the audit record of a hold leaves the reason out.
- Every deletion path asks the same SQL (`RecordsSql.Held`): user deletion (refused and audited `records.deletion_blocked`), the recycle bin purge and retention (the document waits; the first blocked attempt is audited once), and disposition decisions (409 `conflict.records.held`). The `ILegalHolds` contract answers for documents with all of these holds, and keeps the per-resource register other modules use.
- The audit purge asks `ILegalHolds.AuditSelectionsAsync` and stops before the earliest record with a held custodian as actor or target in range, so the chain stays a contiguous verifiable sequence (audit migration 0002). Released holds let it continue.
- After a release, documents whose period ended while they were held go to disposition review rather than being deleted (US7 scenario 3).
- Not built here: tenant closure deletion and data subject erasure have no executor yet; when they are built they must ask `ILegalHolds` before deleting (FR-015). Holds by query and folder holds from the page come later; the API takes a folder already. The records page says that holds cover TeamBeats libraries only.

## Spec 011 R2 slice 2: view-only libraries and guest sharing (T019, T020)
- Two library settings for TeamBeats libraries outside spaces (FR-030): `viewOnly` needs `docs.view_only_watermark` and `guestSharing` needs `docs.guest_sharing` when turned on (Workplace Plus and above). A space library refuses both (400 `validation.documents.space_library`); a library whose default classification is Restricted cannot allow guest sharing (400, and a database check).
- View only applies to readers, meaning people without `doc.contribute` on the document. Contributors and managers still download, so the people who maintain the files keep working. Smallest safe default; the spec does not say who counts as a reader here.
- For a reader of a view-only library the API refuses every way to take a file away: download tokens, controlled copies and the public API file read (403 `authz.denied.documents_view_only`). The document says `downloadable: false` and the page hides those actions.
- The preview carries a watermark with the viewer's name, email and the time (UTC). PDF previews, including converted Office files, have it drawn on every page by the existing PDF stamp; images, video and text show it as an overlay on the page, which is the dynamic watermark the spec describes and no stronger (section 14: a screenshot still leaks). The text is kept on the preview token so the stamp matches what the page shows.
- A share is one folder, one space and an expiry of 1 to 365 days (30 by default). The library manager must own the space, the same rule as compliance projects shared with guest spaces (spec 026 FR-024), so the people reached are the ones that space's owners invited. A folder that inherits gets its own permissions first, so the share reaches exactly that folder and what is under it.
- A share grants reading and nothing more, and only to guests who read the space while it is active; members of the tenant keep what they already had. Guests still never see Confidential or Restricted documents of a library outside spaces (spec 026 FR-009), so a share does not lift that.
- Revoking, expiry and turning guest sharing off end access at once (turning it off revokes every live share). Shares are kept for the record with who created and revoked them. Audited: `doc.shared_with_guest`, `doc.guest_share_revoked`, and `doc.guest_viewed` for every document view and preview by a guest in a library outside spaces (US8 scenario 2), which a guest reaches only through a share.
- The space's files page lists the folders shared with it, linking to the folder in its library.

## Spec 011 R2 slice 3: brand asset portal (T021)
- New built-in role Brand Manager (spec 011 section 4) with `assets.manage` and `assets.view` at tenant scope; tenant administrators hold both. Authorization migration 0037 adds them to existing tenants. The portal needs `docs.asset_portal` (Workplace Plus and above).
- A collection is open to every member (tenant visibility) or restricted to an org unit: the unit and its subtree read it through an ACL entry, and so does each person whose access request was approved. `assets.view` is not in the Employee role. Guests see no collection at all, not even its name (spec 026 FR-009); members see that a restricted collection exists so they can ask for it.
- Assets are images (PNG, JPEG, GIF, WebP, SVG) and the usual template formats (PDF, Word, PowerPoint and its templates, Excel), up to 50 MB and the tenant's upload limit, scanned before they are kept; a file that does not scan clean is refused rather than quarantined. Storage counts toward `docs.storage_gb`.
- Renditions are made on upload through the existing conversion service (Gotenberg's Chromium screenshot and print routes): PNG at 256, 512 and 1024 pixels wide, never wider than the source except the smallest; the SVG itself for an SVG source; and a PDF with the image on a page. The service draws in RGB, so the CMYK PDF the spec mentions "where the source allows" is not made; that needs a colour-managed converter and is left for a later decision. A rendition the service cannot make is marked failed and a brand manager can make them again.
- Usage rights: an optional end date. The document maintenance worker marks ended assets, audits `assets.expired` and publishes `AssetRightsExpired`; from then on readers no longer see the asset in the portal, its downloads or the picker, while brand managers still do. Setting a new end date in the future brings it back.
- Access requests: one open request per person and collection, with a reason; brand managers get a notification and a personal task in their inbox (source `asset_access`) linking to the requests page. Approving adds the person to the collection's ACL; both outcomes notify the requester.
- Every download of a rendition records the person, asset, rendition and time; the report shows the last 90 days by default (up to a year) with totals per asset and rendition. Thumbnails in the portal are not downloads and are not recorded.
- `GET /api/v1/assets/portal` tells the page whether the caller curates, so a brand manager sees the forms before the first collection exists.
- Pages (slice 3b): the content editor's Image button opens a picker of current images the author reads (`/api/v1/assets/picker`); the image block keeps the asset id and its description. Saving checks each image added in that edit through `IAssetImages`; images already on the page stay so the page can still be edited. Readers get `/api/v1/assets/{id}/image`, the largest PNG rendition, checked against the collection and the usage rights on every request and not counted as a download; after the rights end it is gone for everyone, brand managers included, and the browser shows the description instead.
- When rights end, Content finds the pages whose current or draft version shows the asset and tells each page's owner (or its creator when it has none) which page it is, with a link to edit it. The page is not changed. The spec's "page builder picker" is read as this editor picker: the spec 032 builder's `image` property refers to spec 033 theme assets, a separate store, and is left as it is.

## Spec 011 R2 slice 4: bulk import (T022, FR-022)
- A library's managers import a ZIP of up to 4 GB with `manifest.csv` at its top level, into the library or one of its folders. The manifest is UTF-8 CSV with a header row: `path` is required, and `folder` (made when missing), `name`, `type` (a document type of the library, by name or id), `classification`, `created`, `modified` (ISO dates, never in the future, modified not before created), `author` (an email) and `field:{key}` columns are optional. At most 10,000 rows, each path once. A ZIP without a manifest, an unknown column or a row of the wrong width is refused whole, before anything is queued.
- The rows are worked through by a background job, 100 per message, each in its own transaction as the person who started the import. Every row goes through the normal upload checks (file types, size limit, quota, malware scan, sensitive data, duplicates by hash), so a failed row leaves nothing behind and the others carry on. Each row keeps its result: imported, incomplete (stored as a draft only the importer sees because required fields are missing), skipped (already in the library), quarantined, or failed with the reason.
- Original created and modified dates go on the document and its version. The author is kept as the document's creator and the version's author when the email matches a user of the tenant; otherwise the importer stands in and the report says so. The importer stays the owner. Documents of a controlled type come in as drafts and go through release as usual; others are published as 1.0.
- When the last row is done the ZIP is deleted, the import is audited and its starter is notified. The report lists every row, filters by result, and downloads as CSV with fields that start with =, +, - or @ guarded against being read as formulas.

## Spec 011 R2 slice 5: Office editing in the browser (T023, FR-010)
- Office editing is its own feature of the Microsoft 365 connection, `office_editing`, under `integration.microsoft365`. It asks only for delegated `Files.ReadWrite` on the employee's own files, never `Files.ReadWrite.All` or `Sites`. The administrator turns it on and grants consent like the other delegated features, and the employee's existing "Connect Microsoft 365" (the Outlook connect) covers it.
- "Edit in browser" is offered to contributors on Word, Excel and PowerPoint files (.docx, .xlsx, .pptx) up to 250 MB, the Graph simple upload limit. Restricted documents are not offered, because the copy would leave TeamBeats storage. Without the feature or the capability the button is not shown; when the employee has not connected, or must reconnect, the page offers to connect.
- Starting checks the document out (every check-out rule applies) and places a copy of the working file, or the current version, in a "TeamBeats editing" folder of the employee's OneDrive; Office for the web opens it from its web address. While it is open, the normal check-in, discard, working copy and delete answer `conflict.documents.office_editing`.
- Check-in reads the copy back through the normal check-in, so it is scanned, checked for sensitive data and duplicates, and becomes a TeamBeats version with the contributor as author. The copy is removed after the commit, so a failed check-in never loses the edit. Discard releases the check-out and removes the copy. A copy the employee removed from OneDrive answers `conflict.documents.office_copy_gone`, and discard releases the document.
- A library manager's forced check-in ends the edit and leaves the copy in the holder's OneDrive, in line with US2 scenario 3 keeping the holder's changes. A copy that cannot be removed (the employee's Microsoft 365 session ended) stays in their own OneDrive; nothing in TeamBeats depends on it. Starting and ending are audited as `doc.office_edit_started` and `doc.office_edit_ended`.
- Delegated tokens are tied to the TeamBeats session, as for mail and calendar, so checking in from another session first asks the employee to reconnect.

## Spec 011 R2 slice 6: text of PDFs and OCR for search (T026, FR-020)
- New infrastructure: Apache Tika Server (Apache-2.0) with Tesseract, as its own container behind an `ITextRecognizer` port, like Gotenberg for previews. The current stack reads text only from plain text and Office Open XML files; it has no PDF text reader and no OCR, and parsing PDFs or images inside the product would put crafted-file risk in the API process. Tika is configured by `Ocr:Tika:Url` and `Ocr:Tika:Languages` (Tesseract codes, default `eng+ara`). `deploy/docker/tika-ocr.Dockerfile` adds the Arabic language pack to the official full image. Without the setting, PDFs and images give no text, as before.
- PDFs and PNG or JPEG pictures are read after upload by a background job (`documents.text`), because OCR of a long scan takes far longer than an upload should wait. A PDF's own text layer is read in every package; scanned pages and pictures are recognized by OCR only where the tenant has `docs.ocr` (Workplace Plus and above). The text goes on the version and the document is indexed again, so search finds it once the job has run.
- Versions never change, so the text is set once per stored file; a restored version takes the text of the version it restores. A failed or unavailable service leaves the version without text and the document findable by its name, number and metadata; it is not retried.
- Text recognized later is indexed for search only. The sensitive data check of spec 026 still runs at upload on the text the upload can read; OCR text is not checked afterwards. Readers see search results only for documents they may open, as for every other text.

## Spec 011 R2 slice 7: SharePoint connected libraries, part 1 (T024, FR-024 to FR-027, FR-030)
- SharePoint documents is its own feature of the Microsoft 365 connection, `sharepoint_documents`, under `integration.microsoft365`. It asks for application `Sites.Selected` (listing, item permissions and changes, only on sites the customer grants to the TeamBeats application) and delegated `Files.Read.All` (browsing and opening as the person). Upload in slice 3 will need `Files.ReadWrite.All` and is not asked for yet. Allowed sites live in `integrations.sharepoint_site`, at most 50 per connection.
- The integration administrator adds a site by its address. Only https addresses on a `*.sharepoint.com` host, at the root or under `/sites/` or `/teams/`, are accepted, and anything after the site name is dropped; nothing else is sent to Microsoft. TeamBeats resolves the site with the application token, so a site not granted to the application is refused with the same message as a site that does not exist. The connection test fails with "site no longer granted" when an allowed site is withdrawn in Microsoft 365.
- `IDocumentRepository` is now one router in Integrations that picks the adapter by the connection's kind (SharePoint for Microsoft 365, Google Drive for Google Workspace), so Documents never names a system. Two members were added: the sources (connections that serve documents now) and listing a folder as the signed-in person. Document libraries of allowed sites are cached for five minutes per tenant, connection and site list.
- A library manager connects a document library (and optionally one folder of it) at a scope they manage, with names, an audience and a default classification. The audience works as for TeamBeats libraries. Listing and opening run with the person's own delegated access, so SharePoint decides what they see, and a person who has not connected Microsoft 365 is asked to. Opening is audited as `doc.connected_opened`; content is streamed through and never kept.
- Defaults recorded: guests never read connected libraries, because they have no account in the customer's Microsoft 365. An item reached by its ID inside an allowed document library opens if SharePoint lets the person read it, even when the library is rooted at a folder elsewhere in that document library; both checks still apply and nothing is shown that SharePoint would not show the person. Unit and site scope are set at creation and fixed, as for TeamBeats libraries.
- FR-030: upload, TeamBeats folders, document types, bulk import, guest sharing, the view-only watermark, allowed file types and size limits, and records labels are refused for connected libraries with `conflict.documents.connected_library` or `validation.documents.connected_library`. The records manager screens list TeamBeats libraries only. The library page names these actions as not available, with the reason.
- An outage of SharePoint shows the connected library as unavailable and leaves TeamBeats libraries untouched. Still to come in this task: upload, label mapping and the clean-up when a site is removed (part 3).

## Spec 011 R2 slice 8: SharePoint connected libraries, part 2 (T024, FR-027, FR-028, FR-031)
- TeamBeats keeps a derived copy of each connected library in `documents.connected_item`: name, folder, type, size, dates, link and mirrored readers, never content. The maintenance worker queues a sync of each library every two minutes (within the five minute KNO-006 objective) through the `documents.connected_sync` job; it reads the repository's change cursor and walks the whole library again once a day. A change made during a full walk is read again by the next sync, an item moved out of the library's folder leaves the copy with everything under it, and a library is synced up to 50,000 items.
- Change notifications (Graph subscriptions) are not used yet: they need a public notification endpoint per tenant and add little while the change cursor is read every two minutes. They can be added behind the same job when the objective tightens. Delta reports sharing changes on items whose own permissions change; a change to a SharePoint group's members is not an item change, which is one more reason group access is not mirrored (below).
- Mirrored readers only narrow access (ADR-0016). Each item's permissions are read with the application's own access, and people named on the item by email are its readers. Access a person has through a SharePoint site group (Members, Visitors, Owners) or a sharing link is not mirrored, because Microsoft Graph does not list site group members; such items are not found in search until site group members can be read. Opening and listing are not affected, since they run live with the person's own access. Search shows a connected file only to a person in the library's audience, who is not a guest, and whom the mirrored readers name.
- Search indexes the name, the folder path and the file type, with the library's default classification. File text is not indexed yet; reading content with the application's access and the text service of slice 6 is a later step. A result opens a TeamBeats page that checks the audience and the mirrored readers again and then sends the person to the file in SharePoint, which checks once more.
- When SharePoint cannot be read during a sync the library is marked degraded since that moment, the integration administrators are told once (`documents.connected_degraded`), and the next successful sync clears it. When the live listing fails the library page shows the last synced copy of the folder, filtered by the mirrored readers (folders always show), with how old it is; files cannot be opened until SharePoint answers.
- The fake Microsoft 365 of the development host serves SharePoint sites from the scenario file (`sharePointSites`), and the staging scenario grants `Sites.Selected` with a Human Resources site (granted) and a Finance site (not granted), so the feature can be tried on staging and in the browser tests.

## Spec 011 R2 slice 9: SharePoint connected libraries, part 3 (T024, FR-029, FR-032)
- Uploads are their own Microsoft 365 feature, `sharepoint_upload`, with the delegated `Files.ReadWrite.All` permission, so a tenant that only reads connected libraries never grants write access. A library manager turns uploads on per connected library. Then people who may contribute to the library in TeamBeats upload files (a file of the same name becomes its new version) and rename files and folders; each write runs as the person in SharePoint, so SharePoint's permissions decide as well, and TeamBeats keeps no copy. The library is synced again straight after a write. A single upload takes up to 250 MB, the size SharePoint accepts in one request; larger files are uploaded in SharePoint itself. Metadata columns of SharePoint libraries are not edited from TeamBeats yet, only names. The sensitive data warning of spec 026 is not applied to connected uploads, because SharePoint's own data loss prevention applies there.
- Purview labels: the sync reads each file's `sensitivityLabel` (label ID and display name) with the item, so no extra Microsoft permission is needed and labels are listed for mapping as synced files carry them. If Microsoft does not return the property for a tenant, files count as unlabelled and take the library's default, which is the documented rule for unlabelled files. The mapping is kept per Microsoft 365 connection by the integration administrators (`integration.manage`), because labels belong to the connection's directory. An unmapped label counts as confidential. Changing a mapping indexes that label's files again.
- Removing an allowed site archives each connected library whose document library the connection no longer serves, deletes its synced copy and drops its files from search; deleting the connection does the same for all its libraries and removes its label mapping. Turning the feature off does not archive anything, because it may be turned on again; the libraries show SharePoint as unavailable meanwhile. The archived libraries keep their audience and audit history.
