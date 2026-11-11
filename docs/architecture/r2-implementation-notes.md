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
