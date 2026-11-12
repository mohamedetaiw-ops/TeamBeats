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
