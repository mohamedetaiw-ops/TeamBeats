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
