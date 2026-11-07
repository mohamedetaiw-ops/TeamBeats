# Feature Specification: Connector Framework and Integration Packs

| Field | Value |
|---|---|
| Spec ID | 017-connector-framework |
| Area | integrations |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R2 (common connector contract and runtime, connection types beyond Microsoft 365, custom REST API connector, outbound webhooks and inbound signed webhooks, connector actions for workflows and agents, sync jobs with retry, replay and status, on-premises sync agent for Active Directory over LDAPS, Slack, Google Workspace directory and Drive extension points, clock data import and ERP hand-off for spec 022, KPI feed contract), R3 (Oracle Fusion and SAP integration packs, MCP server registration for agents, connector marketplace, KPI tiles on location dashboards) |
| Last updated | 2026-11-07 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | INT-001, INT-004, INT-005, INT-006, INT-009, INT-010, CON-001 to CON-024 (new) |
| Use cases | UC-07, UC-10 |
| Depends on | 004 (authorization), 005 (entitlements), 006 (Connections area, FR-010 to FR-015), 013 (no model calls from connectors), 015 (alerts), 024 (service tasks, messages), 026 (step-up, SIEM), 027 (connector actions as agent tools) |
| Used by | 002 and 003 (on-premises AD), 011 (document repository contract), 016 (fulfilment steps), 019 (HR integration), 021 (KPI feeds), 022 (clock data, ERP hand-off), 024 (service tasks and message correlation), 027 (connector tools) |

## 1. Problem and outcome

**Problem:** Every enterprise customer asks for the same thing in a different system: create a ticket in ServiceNow, hand an expense to SAP, read clock data from the attendance machine, post to Slack, read users from an on-premises Active Directory. If each feature calls these systems its own way, credentials end up in configuration screens, failures are silent, retries create duplicates, and nobody can tell which system received which data. The constitution (XVII) already says external systems go through connector adapters with canonical contracts; R1 built that only for Microsoft 365.

**Outcome:** One connector framework in the `Integrations` module runs every external system behind canonical contracts. An integration administrator creates a connection in the Connections area (spec 006), stores credentials in the secret manager, tests it, and turns on features. Workflows (016, 024) and agents (027) call connector actions by name with mapped fields, never raw HTTP. Every call and sync is retried with backoff, idempotent, visible in a run history, replayable, and audited. A custom REST connector lets a tenant reach a system we have no pack for, without code. A small outbound sync agent reaches on-premises systems from SaaS without opening inbound firewall ports.

## 2. Scope

**In scope:**
- Common connector contract: connection type, features, actions (with input and output schemas and side effects), triggers (inbound events), sync jobs, health check, and canonical capability contracts (directory, document repository, ticketing, HR record, finance hand-off, time and attendance, messaging, KPI feed).
- Connection lifecycle in the Connections area: create, consent or credentials, test per feature, enable, disable, delete with dependents (spec 006 FR-010 to FR-015 apply to every type).
- Credentials as secret manager references only; OAuth 2.0 client credentials and authorization code, API key, basic auth over TLS, mutual TLS certificates; rotation and expiry alerts. *(INT-010)*
- Runtime: durable jobs on the outbox and workers, retry with backoff and jitter, rate limits per connection, circuit breaker, idempotency keys, dead letter with replay. *(INT-009)*
- Run history per connection: each call and sync with status, duration, counts, error class and correlation ID, without payload content by default.
- Custom REST API connector: base URL on a tenant allowlist, auth method, actions defined by method, path template, headers, JSON body template and response mapping, tested with sample data.
- Outbound webhooks: signed payloads (HMAC SHA-256 with timestamp), retries, host allowlist, delivery log. Inbound webhooks: per-connection URL and secret, signature and replay window checks, mapping to a product event or a process message (spec 024).
- Connector actions as workflow steps (016 US8, 024 FR-010) and as agent tools (027 AGV-006), each registered with its side effect and approval rule.
- On-premises sync agent: an outbound-only service a customer installs inside their network, which connects to TeamBeats over TLS with a per-agent certificate, pulls jobs and returns results; first used for Active Directory over LDAPS (specs 002 and 003).
- Packs in R2: on-premises Active Directory, Slack (notifications and share), Google Workspace directory and Drive (extension points for INT-004 through the same document repository contract as ADR-0016), generic SFTP and CSV import for clock data (022), generic finance hand-off over REST (022 expenses).
- KPI feed contract (R2 contract, R3 tiles): a connector publishes named metric values per location for spec 021.

**Out of scope:**
- Microsoft 365 features already specified in specs 006, 011 and 018 (they move onto this runtime without behavior changes).
- HR system specifics (spec 019 builds on this framework).
- Oracle Fusion and SAP packs (R3, INT-005, INT-006), MCP servers for agents (R3, spec 027).
- Running customer code: no script steps, no custom code in connectors.
- An integration platform for system-to-system flows that never touch TeamBeats.

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Integration administrator | Create, test, enable, disable and delete connections; manage credentials (step-up), webhooks, custom REST actions, sync schedules; replay failed runs | `integration.manage` at tenant scope |
| Integration operator | See run history and health, replay failed runs, pause a sync | `integration.operate` |
| Service owner and process owner | Use enabled connector actions in their routes and processes; see the actions' fields and side effects | `service.manage` or `process.design`, plus the action enabled for their scope |
| Security officer | See connection inventory, credential age and the audit trail | `security.view`, `audit.read` |
| AI Policy Administrator | Allow connector actions as agent tools within spec 027 rules | `ai.policy_manage` |
| On-premises sync agent | Pull jobs for its connection, return results | Agent certificate bound to one connection and tenant |
| Platform operator | Publish and update connector packs, disable a pack for all tenants | Platform |

## 4. User scenarios

### User Story 1: Connect a system and see that it works (Priority: P1, R2)

Ahmed, integration administrator at Acme, adds a ServiceNow connection with the ticketing pack: base URL, OAuth client credentials stored in the secret manager, and the "create incident" and "get incident status" actions. He tests it and turns it on.

**Why this priority:** every R2 integration starts with a trusted connection.

**Independent test:** create a connection of a pack type, store credentials, test each feature against a fake system, enable it, then disable it and confirm calls stop.

**Acceptance scenarios:**
1. **Given** the pack list, **When** Ahmed adds a connection, **Then** he sees only packs his package includes, each with the actions, triggers and data it reads and writes, before entering any credential.
2. **Given** credentials entered, **When** he saves, **Then** step-up is required (spec 026), the secret is stored as a secret manager reference, and it is never shown again or written to logs, audit or exports.
3. **Given** a saved connection, **When** he runs Test connection, **Then** each feature is checked separately with a plain result per feature, and a warning shows when the credential grants more than the features need, where the system can tell.
4. **Given** an enabled connection, **When** he disables it, **Then** external calls stop within 1 minute, workflows waiting on its actions pause with "connection disabled", and agents holding its tools show a degraded notice.
5. **Given** a credential with an expiry date, **When** 14 days remain, **Then** integration administrators are alerted, and the status becomes "credential expiring".

### User Story 2: Workflows and agents call connector actions safely (Priority: P1, R2)

The "Laptop request" service ends with "create incident in ServiceNow" with the requester and device mapped. A network timeout happens after ServiceNow already created the incident.

**Acceptance scenarios:**
1. **Given** a connector action step, **When** the route runs, **Then** only the fields the owner mapped are sent, the call carries an idempotency key derived from the request and step, and the external reference is stored on the request.
2. **Given** a timeout after the external system acted, **When** the step retries with the same idempotency key, **Then** no second incident is created where the system supports idempotency, or the action's status check finds the existing one; otherwise the step stops with "outcome unknown" and an incident is raised for the operator (spec 024).
3. **Given** a business error from the system (for example "invalid category"), **Then** it maps to the action's declared business error so the route can branch, and is not retried.
4. **Given** a technical error (timeout, 5xx, 429), **Then** the call retries with backoff and jitter up to the action's limit, then the step raises an incident with a replay option.
5. **Given** an agent tool backed by the same action, **When** the agent calls it, **Then** the spec 027 tool gateway checks run first, and the connector receives only the validated parameters.

### User Story 3: Know what happened and replay failures (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** a connection, **When** an operator opens run history, **Then** each call and sync shows time, action, status, duration, record counts, error class and correlation ID, filterable by status and action, without payload content unless the tenant enabled payload capture for debugging.
2. **Given** failed runs in the dead letter list, **When** the operator replays one or a selection, **Then** each runs again with its original idempotency key, and the outcome is recorded next to the original.
3. **Given** a sync job that failed halfway, **When** it is replayed, **Then** it resumes from its last checkpoint and does not duplicate records.
4. **Given** repeated failures over the threshold, **Then** the circuit opens for that connection, its status becomes degraded, dependent features show their degraded state, and administrators are alerted (spec 006 FR-014).

### User Story 4: Reach a system with no pack (Priority: P2, R2)

Mona wants a "check parking permit" action against the facilities system's REST API.

**Acceptance scenarios:**
1. **Given** a custom REST connection, **When** Mona defines an action with method, path template, headers, body template and response mapping, **Then** she can test it with sample input and see the mapped output before saving.
2. **Given** a base URL host not on the tenant allowlist, or a private network address in shared SaaS, **Then** saving is refused. *(SSRF protection)*
3. **Given** a template field that refers to an input not declared in the schema, **Then** saving is refused with the field named.
4. **Given** an action marked "writes data", **Then** it is a write action for workflows and agents and gets the stricter approval defaults of spec 027.

### User Story 5: Send and receive webhooks (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** an outbound webhook for `RequestCompleted`, **When** the event happens, **Then** a signed payload with the selected fields is sent to the allowlisted URL, retried on failure, and recorded in the delivery log.
2. **Given** an inbound webhook URL for a connection, **When** a request arrives with a valid signature and a timestamp within 5 minutes, **Then** it is mapped to the configured product event or process message; an invalid signature, an old timestamp or a repeated delivery ID is refused and counted.
3. **Given** the secret is rotated, **Then** the old secret stops working at once, and both are accepted for an optional overlap the administrator sets (at most 24 hours).

### User Story 6: Reach systems inside the customer network (Priority: P1, R2)

Acme runs Active Directory on premises and uses TeamBeats SaaS.

**Acceptance scenarios:**
1. **Given** a new on-premises connection, **When** the administrator downloads the sync agent, **Then** it comes with a one-time enrolment code; after enrolment it holds a certificate bound to that tenant and connection and connects outbound only.
2. **Given** an enrolled agent, **When** a directory sync job is due, **Then** the agent pulls it, reads AD over LDAPS with the service account stored on the customer side, and returns canonical directory records (spec 003 US6).
3. **Given** the agent is offline, **Then** the connection status is degraded with the last contact time, and jobs wait without failing other features.
4. **Given** an agent certificate is revoked, **Then** the agent can no longer pull jobs within 1 minute.
5. **Given** two agents for one connection, **Then** jobs are taken by one at a time, so a sync is never run twice in parallel.

### User Story 7: Import clock data and hand off to finance (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** an SFTP or CSV clock data source with a column mapping, **When** the daily import runs, **Then** records map to employees by employee number, unmatched rows are listed, and spec 022 receives canonical clock events.
2. **Given** an approved expense claim (spec 022), **When** hand-off runs, **Then** the finance hand-off contract sends the mapped fields, stores the external reference, and later status checks update payment status.

### User Story 8: Post to Slack (Priority: P3, R2)

**Acceptance scenarios:**
1. **Given** a Slack connection, **Then** spec 015 can deliver notifications to a user's Slack direct messages where the user linked their account, and communicators can share a content link to a channel they choose.
2. **Given** a Slack user who left the workspace, **Then** delivery falls back to the user's other channels.

### Edge cases and exception flows
- External system changes its API version: the pack declares supported versions; an unsupported version fails the test with a clear message.
- External rate limits: per-connection rate limits and the system's own retry advice are honored; bursts queue.
- Partial sync failure: per-record errors are recorded and counted; the job succeeds with warnings when below the error threshold.
- Clock skew on inbound webhooks: a 5 minute window, configurable down to 1 minute.
- Deleted connection with running jobs: jobs are cancelled, dependents shown before delete; owning specs decide what happens to derived data.
- Pack disabled by the platform operator for a vulnerability: all its connections stop and tenants are notified.

## 5. Functional requirements

- **CON-001 / FR-001**: Every external system MUST be reached only through a connector in the `Integrations` module implementing a canonical capability contract; product modules MUST NOT see external DTOs. *(INT-001, constitution XVII)*
- **CON-002 / FR-002**: A connection MUST be a tenant-owned record with features turned on separately, each declaring the data it reads and writes and the external permissions it needs. *(INT-001)*
- **CON-003 / FR-003**: Credentials MUST be secret manager references, never shown after saving or written to logs, audit, exports or prompts; saving or replacing MUST require step-up. *(INT-010)*
- **CON-004 / FR-004**: Test connection MUST check each feature separately and warn about broader permissions where detectable.
- **CON-005 / FR-005**: Connector calls and syncs MUST run as durable jobs with retry, backoff with jitter, idempotency keys, per-connection rate limits and a circuit breaker. *(INT-009)*
- **CON-006 / FR-006**: Failed jobs MUST go to a dead letter list and MUST be replayable with their original idempotency keys. *(INT-009)*
- **CON-007 / FR-007**: Every call and sync MUST be recorded in run history with status, duration, counts, error class and correlation ID; payload capture MUST be off by default and time-boxed when on.
- **CON-008 / FR-008**: Connector actions MUST declare input and output schemas, side effect (read, write, external effect), business errors and idempotency support.
- **CON-009 / FR-009**: Workflow steps and agent tools MUST call connector actions by name with mapped fields only. *(016 FR-016, 024 FR-010, 027 AGV-006)*
- **CON-010 / FR-010**: An action with unknown outcome after retries MUST stop with "outcome unknown" and raise an incident for write actions.
- **CON-011 / FR-011**: The custom REST connector MUST support method, path template, headers, JSON body template, response mapping and testing with sample data, with no code.
- **CON-012 / FR-012**: Outbound hosts MUST be on the tenant allowlist; in shared SaaS, private, loopback and link-local addresses MUST be refused after DNS resolution.
- **CON-013 / FR-013**: Outbound webhooks MUST be signed with HMAC SHA-256 over the timestamp and body, retried, and logged.
- **CON-014 / FR-014**: Inbound webhooks MUST verify signature, timestamp window and delivery ID uniqueness before mapping to a product event or process message. *(024 FR-009)*
- **CON-015 / FR-015**: Secret rotation MUST invalidate the old secret at once unless an overlap of at most 24 hours is set.
- **CON-016 / FR-016**: The on-premises sync agent MUST connect outbound only, enrol with a one-time code, hold a certificate bound to one tenant and connection, and support revocation within 1 minute.
- **CON-017 / FR-017**: Jobs for one connection MUST be taken by one agent at a time.
- **CON-018 / FR-018**: The on-premises Active Directory pack MUST read users, groups and organization attributes over LDAPS and return the canonical directory contract used by specs 002 and 003.
- **CON-019 / FR-019**: The document repository contract of ADR-0016 MUST be the extension point for Google Drive and other document systems. *(INT-004)*
- **CON-020 / FR-020**: Clock data import MUST support SFTP and CSV with column mapping and report unmatched rows.
- **CON-021 / FR-021**: The finance hand-off contract MUST send mapped fields, store the external reference and update status.
- **CON-022 / FR-022**: The KPI feed contract MUST carry metric name, value, unit, period, location and source time.
- **CON-023 / FR-023**: Disabling a connection or feature MUST stop external calls within 1 minute; dependents MUST show their degraded state. *(006 FR-015)*
- **CON-024 / FR-024**: Connectors MUST NOT call models; any AI processing of connector data goes through the AI Gateway in the consuming feature.

## 6. Business and validation rules
- Default retry: 5 attempts over about 30 minutes for jobs; 3 attempts within 10 seconds for actions called inside a user request.
- Default circuit breaker: opens after 50% failures over 20 calls in 5 minutes, half-open after 2 minutes.
- Dead letter retention 14 days; run history 90 days; payload capture windows at most 24 hours, Public and Internal data only.
- Custom REST actions: response mapping limited to JSON paths; no expressions beyond the FEEL subset of spec 024.
- Inbound webhook bodies at most 1 MB.

## 7. Tenant and organization scope
- Tenant-owned data: connections, features, credential references, actions, webhooks, run history, dead letters, agent enrolments.
- Organization scoping: a connection can be limited to org units for use in routes and agents; administration is tenant-wide.
- Cross-tenant denial cases: a sync agent certificate of tenant A cannot pull tenant B's jobs; an inbound webhook URL resolves to exactly one tenant and connection; run history and dead letters are never visible across tenants.
- Sibling, parent and child cases: a connection limited to Acme Gulf cannot be used in an Acme Africa service route.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Connection, features, actions | Product | Internal | Life of tenant | No |
| Credential | Secret manager | Restricted | Until rotated or deleted | No |
| Run history | Product | Internal | 90 days | Possibly (IDs) |
| Captured payloads (debug) | Product, encrypted | Up to Internal | 7 days | Possibly |
| External references on requests | Product | Same as the request | Same as the request | No |
| Sync agent enrolment | Product | Confidential | Life of the agent | No |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `integration.connectors` (framework, packs in the package) | off | on | on |
| `integration.custom_rest` | off | on | on |
| `integration.webhooks` | off | on | on |
| `integration.onprem_agent` | off | on | on |
| `integration.max_connections` (limit) | 0 | 10 | unlimited |
| `integration.erp_packs` (Oracle, SAP, R3) | off | off | on |

Microsoft 365 features keep their own keys (`integration.microsoft365`, spec 006).

## 10. UX, localization and accessibility
- The Connections area shows every connection with type, features, status, last success and owner; a connection page has tabs for features, credentials, actions, webhooks, run history and dependents.
- Errors are shown as plain classes ("the system refused the credential", "the system is not responding") with the technical detail behind a disclosure.
- All screens in Arabic and English, RTL, keyboard operable; mappings use field pickers, not free text.

## 11. AI behavior
Connectors never call models. Connector outputs passed to agents are untrusted data (spec 027 US5).

## 12. Integrations and events
- Publishes `ConnectionCreated`, `ConnectionDeleted`, `ConnectionFeatureChanged`, `ConnectorUnavailable`, `ConnectorRestored`, `ExternalRecordSynced`, `ConnectorActionCompleted`, `InboundWebhookReceived`.
- Consumes product events selected for outbound webhooks and job requests from 016, 019, 022, 024 and 027.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| integration.connection_created / deleted / enabled / disabled | audit | connection, type, actor |
| integration.credential_changed | audit | connection, credential name, actor (no secret) |
| integration.action_defined / changed | audit | connection, action, side effect, actor |
| integration.webhook_secret_rotated | audit | connection, overlap, actor |
| integration.agent_enrolled / revoked | audit | connection, agent, actor |
| integration.replay | audit | runs, actor |
| integration.call | telemetry | connection type, action, status, latency (no content) |

## 14. Security and abuse cases
- Server-side request forgery through custom REST: allowlist plus resolved address checks (FR-012).
- Credential theft: secret manager only, step-up to change, never displayed.
- Replay of inbound webhooks: signature, timestamp window and delivery ID uniqueness.
- Data exfiltration through mapped fields: only selected fields are sent; mappings are audited.
- Compromised sync agent: certificate bound to one connection; revocation within 1 minute; the agent only receives jobs for its connection.
- Malicious pack update: packs are platform-signed and versioned; the platform operator can disable a pack everywhere.

## 15. Non-functional requirements
- Connector overhead under 30 ms p95 excluding the external call.
- 1,000 queued jobs per minute per installation in R2.
- Sync of 20,000 directory users through the on-premises agent under 30 minutes (matches spec 002 T027).

## 16. Success criteria
- **SC-001**: A connector-down test suite (constitution XXVIII) passes on every build: credential revoked, throttled, unavailable, partial failure, duplicate delivery.
- **SC-002**: An integration administrator connects a new REST system and uses it in a service route in under 1 hour without developer help.

## 17. Open questions
- [NEEDS CLARIFICATION: which ticketing systems get first-party packs in R2?] Default: ServiceNow and Jira Service Management; others through custom REST.
- [NEEDS CLARIFICATION: does the sync agent ship for Windows only or also Linux?] Default: Windows service first (AD hosts), Linux container second.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| INT-001 | CON-001, CON-002, US1 |
| INT-004 | CON-019 |
| INT-005, INT-006 | R3 packs (section 2) |
| INT-009 | CON-005 to CON-007, US2, US3 |
| INT-010 | CON-003, CON-015 |
| UC-07 | US2 |
| UC-10 | CON-019, ADR-0016 |
