# Implementation Plan: Connector Framework and Integration Packs

**Spec:** `specs/017-connector-framework/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0006 (outbox), ADR-0013, ADR-0016, spec 006 plan (Connections area), spec 018 plan (Microsoft 365 adapter with its own retry, to move onto this runtime)

## Summary
The `Integrations` module gains a connector runtime: pack registrations in code (`IConnectorPack` with features, actions, triggers and sync jobs), connection records, a job runner on the outbox workers with retry, idempotency, rate limits and circuit breakers, run history, dead letters and replay. The Microsoft 365 adapter moves onto the runtime first, unchanged in behavior. Then the custom REST connector, webhooks, connector actions for 016 and 024 and tools for 027, the on-premises sync agent with the AD pack, clock data import, finance hand-off and Slack.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I tenant isolation | Yes | Connections, runs and dead letters tenant-owned with forced RLS; agent certificates bound to one tenant and connection |
| III authorization | Yes | `integration.manage`, `integration.operate` through `IAuthorizationService`; step-up on credentials |
| XVII connectors | Yes | Canonical contracts only; packs translate at the edge |
| XVIII durability | Yes | Jobs on the outbox with idempotent handlers and replay |
| Rule 9 secrets | Yes | Secret references only; the secret manager adapter is the single reader |

## Data model (schema `integrations`, extending R1)
- `connection` gains `pack`, `pack_version`, `org_unit_limits`, `rate_limit`.
- `connector_action` (custom REST definitions), `webhook_out`, `webhook_in`, `run` (status, counts, error class, correlation ID, checkpoint), `dead_letter`, `payload_capture` (encrypted, time-boxed), `sync_agent` (certificate thumbprint, last contact, revoked_at).

## Runtime
- `IConnectorRuntime.InvokeAsync(connectionId, action, input, idempotencyKey)` from `Integrations.Contracts` for 016, 024 and 027; jobs for syncs.
- Retry policy per call site (section 6 defaults), circuit breaker per connection in memory with state shared through the database for multi-instance installs.
- SSRF guard: allowlist plus resolved address check on every connection open, in one `SafeHttpClient` handler used by all packs.

## Sync agent
A .NET worker service published as a signed Windows service and Linux container, talking gRPC over TLS with client certificates to a dedicated endpoint; enrolment by one-time code; long-poll for jobs; the AD pack runs inside it with `System.DirectoryServices.Protocols` (LDAPS), returning canonical records. No new third-party library.

## Packs in R2
Microsoft 365 (moved), custom REST, outbound and inbound webhooks, on-premises AD, ServiceNow and Jira Service Management ticketing, Slack, Google Workspace directory and Drive (extension point through the document repository contract), SFTP and CSV clock data (SSH.NET, MIT, for SFTP; the base library has no SFTP client), generic finance hand-off over REST.

## Tests
Fake external systems per pack in `DevFakes` with faults; connector-down suite (SC-001) on every build; SSRF tests with private addresses and DNS rebinding; idempotent replay; inbound signature, timestamp and duplicate checks; agent enrolment, revocation and single-taker; cross-tenant runs and certificates. Acceptance (e2e): create, test, enable and disable a connection; define a custom REST action; read run history and replay, in English and Arabic with axe.
