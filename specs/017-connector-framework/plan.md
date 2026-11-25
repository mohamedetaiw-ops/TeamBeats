# Implementation Plan: Connector Framework and Integration Packs

**Spec:** `specs/017-connector-framework/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0006 (outbox), ADR-0013, ADR-0016, spec 006 plan (Connections area), spec 018 plan (Microsoft 365 adapter with its own retry, to move onto this runtime)

## Summary
The `Integrations` module gains a connector runtime: pack registrations in code (`IConnectorPack` with features, actions, triggers and sync jobs), connection records, a job runner on the outbox workers with retry, idempotency, rate limits and circuit breakers, run history, dead letters and replay. The Microsoft 365 adapter moves onto the runtime first, unchanged in behavior. Then the custom REST connector, webhooks, connector actions for 016 and 024 and tools for 027, the on-premises sync agent with the AD pack, clock data import, finance hand-off and Slack.

## R3 plan
**Date:** 2026-11-25. R3 adds the Oracle Fusion and SAP integration packs (INT-005, INT-006), the connector marketplace and KPI tiles on location dashboards (header, target release R3). MCP server registration for agents is built by spec 027 R3 T022 (`mcp_server` pack), so it is recorded here as met.

R3 slices, in order:
1. **Oracle Fusion and SAP S/4HANA finance packs:** two named packs behind the finance hand-off contract (FR-021), with the same `submit_expense` and `get_payment_status` actions as the generic pack, so spec 022 expenses do not change. `finance_oracle_fusion` posts a Standard supplier invoice to Payables (`/fscmRestApi/resources/11.13.18.05/invoices`) per claim, numbered by the claim, for the employee's supplier number in the configured business unit, one Item line per expense line charged to the category's distribution combination. The status comes from `PaidStatus`, `ApprovalStatus` and `CanceledFlag`. `finance_sap_s4` posts a supplier invoice through the Supplier Invoice OData API (`API_SUPPLIERINVOICE_PROCESS_SRV`, `A_SupplierInvoice`) in the configured company code, one G/L account line per expense line, after fetching the CSRF token the service requires. Its status comes from `SupplierInvoiceStatus`. Both look up an earlier attempt's invoice before posting again, so a submit is idempotent. The hand-off uses the oldest enabled connection of any finance kind.
2. **Connector marketplace:** a page in the Connections area listing every published pack with its group, version, actions and their side effects, the capability it needs and whether the package has it, and how many connections the tenant has; Add opens the wizard. The platform operator can turn a pack off for all tenants with a reason (section 3); a pack turned off cannot be added and its connections make no calls until it is turned on again.
3. **KPI tiles on location dashboards:** built as spec 021 R3 (FR-010, US6 scenario 2) on the KPI feed contract (FR-022).

Defaults (spec silent):
- The capability keys are the catalogue keys of spec 005, `integration.oracle_fusion` and `integration.sap` (both Enterprise and Government only), which spec 005 sells as separate add-ons. Section 9 names them together as `integration.erp_packs`; one key per system keeps the add-ons of spec 005 working, so no new key is added. This is recorded as a spec difference for review.
- The first named packs are finance hand-off packs, because that is the contract spec 022 uses today. HR packs for Oracle HCM and SuccessFactors are spec 019 R3.
- Payment of an SAP invoice is not in the Supplier Invoice API, so SAP reports posted as approved and never paid unless the tenant maps a status value to paid. Reading clearing from the journal entry items is a later step.
- Status words are configurable per connection, with documented defaults, since customers configure approval states differently.
- Marketplace entries are TeamBeats packs only. No third-party packs and no customer code (section 2, out of scope).

### R3 data model (schema `integrations`)
- `platform_pack_state`: platform data, no tenant column. The pack kind, turned off, reason, by whom and when.

### R3 tests
- Each ERP pack: the claim becomes the ERP's invoice with only the declared fields, the category account or the default, the reference is stored and the status is read until paid or approved; a retry after a timeout finds the first invoice; SAP writes carry the CSRF token and cookie and a refused token is retried; settings are checked; the pack needs its capability and stops when it is turned off; another tenant never reaches the connection.
- Marketplace: lists packs with package state; a pack the platform turned off cannot be added and its calls stop; only the platform operator turns packs off; another tenant sees only its own connection counts.

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
- `IWebhookDelivery` in `Integrations.Contracts` sends a signed outbound webhook with retry and delivery log; spec 035 event subscriptions use it with thin payloads.

## Sync agent
A .NET worker service published as a signed Windows service and Linux container, talking gRPC over TLS with client certificates to a dedicated endpoint; enrolment by one-time code; long-poll for jobs; the AD pack runs inside it with `System.DirectoryServices.Protocols` (LDAPS), returning canonical records. No new third-party library.

## Packs in R2
Microsoft 365 (moved), custom REST, outbound and inbound webhooks, on-premises AD, ServiceNow and Jira Service Management ticketing, Slack, Google Workspace directory and Drive (extension point through the document repository contract), SFTP and CSV clock data (SSH.NET, MIT, for SFTP; the base library has no SFTP client), generic finance hand-off over REST.

## Tests
Fake external systems per pack in `DevFakes` with faults; connector-down suite (SC-001) on every build; SSRF tests with private addresses and DNS rebinding; idempotent replay; inbound signature, timestamp and duplicate checks; agent enrolment, revocation and single-taker; cross-tenant runs and certificates. Acceptance (e2e): create, test, enable and disable a connection; define a custom REST action; read run history and replay, in English and Arabic with axe.
