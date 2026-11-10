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
