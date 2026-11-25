# R3 implementation notes

What was built for R3, slice by slice, and where it differs from the specs and plans. Each difference is either covered by an ADR or listed as a follow-up.

## Spec 026 R3 slice 1: four-eyes approval of critical settings (FR-016, T032 to T034)

- The spec names the critical settings and nothing else; the defaults are in the spec 026 R3 plan. The switch is a tenant setting of its own (`security.four_eyes`), off by default. Turning it on needs two active people who hold `security.manage`, so a tenant with one administrator cannot lock itself out; turning it off is held like any other change.
- Held kinds: `security.policy` (`security.manage`), `identity.provider` (`identity.config_manage`), `audit.retention` (`audit.retention_manage`) and `security.four_eyes`. Key changes join as `security.keys` with customer-managed keys (slice 2).
- `IFourEyes` and `IFourEyesApplier` are in `Security.Contracts`. The owning endpoint checks the request as usual (validation, ETag, permission, step-up where the endpoint has it) and then calls `HoldAsync`; with the switch on it stores the request in `security.pending_change` (migration 0009) and answers 202 with the pending change. Identity and Audit reach Security only through these contracts. Requests hold no secrets (the sign-in configuration carries a secret reference only).
- Approving needs the kind's permission over the tenant, a fresh sign-in, and not being the requester. The applier runs as the approver through the direct path, so the owning module validates again and writes its usual audit entry; Security audits `security.change_requested`, `_approved`, `_rejected`, `_withdrawn` and `_expired` with requester and decider. A change whose apply fails stays pending.
- One pending change per kind (`conflict.four_eyes.pending`, unique index). Changes expire after 72 hours: on the hourly access review sweep, and on listing and holding.
- The other holders of the permission get a security notice when a change waits; the requester gets one when it is decided or expires. Withdrawing tells no one.
- Pages: Security > Approvals (`/admin/security/changes`) with the switch and the list; the policy page shows a notice while a policy change waits and says "waits for approval" after a held save. The identity and retention pages do not have their own notice yet; the approvals page lists every kind.

## Spec 026 R3 slice 2: customer-managed keys (US8, FR-007, T035 and T036)

- Defaults are in the spec 026 R3 plan: Enterprise and Government only (`security.customer_keys`), Azure Key Vault or Managed HSM and HashiCorp Vault (or OpenBao) transit, one 256-bit data key per tenant stored only wrapped (`security.tenant_key`, migration 0010), a check every 5 minutes, the plaintext data key in memory for at most 15 minutes, a refusal by the key service is a revocation, an outage is not (until 24 hours without an answer), no removal of a key.
- `IKeyManagement` (Integrations.Contracts) with `KeyManagementService` in Integrations: plain REST to Key Vault (`wrapkey`, `unwrapkey`, key `GET`, Entra client credentials, token cached until a minute before it expires) and to Vault transit (`encrypt`, `decrypt`, `keys`). It uses the connector HTTP client, so the private address guard applies; it is not a connector pack, because the run history would record payloads and a data key must never be recorded. No SDK was added.
- `TenantKeyService` (Security) connects and changes the key (validated, held under four-eyes as `security.keys`, must wrap and unwrap, the same data key moves to a new key), runs the check (rotation onto a newer key version, revocation, restore, outage, each audited once and alerted), and implements `ITenantDataKeys` (Security.Contracts) for other modules.
- `TenantKeyGate` runs after the network rules: while the key is revoked every tenant request answers 503 `tenant.key_unavailable` except `GET /api/v1/site/availability` and health checks. The sign-in page reads the availability notice and sends people to the sign-in problem page with reason `key_unavailable`. The status is cached for 30 seconds per instance, so a revocation found by a check reaches every instance within 30 seconds.
- What the data key protects: AI debug log entries (`ai.debug_log.data_key_id`, migration 0010 of AI Gateway). When the key is revoked nothing new is captured and stored entries show no text; they are not deleted. Moving other content (documents in object storage, search and vector indexes, backups) onto the tenant data key is a follow-up: those stores rely on the database and storage encryption at rest today, which the platform manages.
- Background jobs of a tenant whose key is revoked keep running on content that is not under the data key; only requests are refused. A follow-up can pause tenant jobs while revoked.
- Pages: Security > Encryption key (`/admin/security/keys`) with the status, a check now button and the connect or change form; Security > Approvals lists held key changes. Alerts `key_revoked` and `key_unreachable` join the alert list.


## Spec 026 R3 slice 3: on-prem hardening pack (T037)

- Defaults are in the spec 026 R3 plan: `Platform:Hosting=customer` turns on four installation checks on the posture page (production mode, database TLS, durable messaging, private addresses blocked). They are read from configuration by `InstallationPosture` (Security) and appended after the tenant checks; they carry no fix link.
- The guide (`docs/security/hardening-guide.md`) and the matrix (`docs/security/shared-responsibility.md`) cover what the product cannot see: patching, backups, volume encryption, network segmentation, secret management.
- Gap: the checks cover the settings the API process can read. Settings of other containers (RabbitMQ users, ClamAV signatures, proxy TLS) are left to the operator's own checks.
- With this slice spec 026 R3 is built except FR-019 (tenant export and deletion certificate), which stays a known gap in the evidence.

## Spec 011 R3 slice 1: records and disposition certificates (T027, T028)

- Defaults are in the spec 011 R3 plan. Migration 0018 of Documents adds `declares_record` to retention labels, `record_at`, `record_by` and `record_source` to documents, and `disposition_certificate` with its items.
- A record refuses check-out, working copies, check-in, restore, browser editing, metadata changes and deletion with `conflict.documents.record`; the checks sit in the same guards as archived documents (`DocumentVersions.EditableAsync`, `DocumentFiles.UpdateMetadataAsync`, `DocumentVersions.DeleteAsync`, `OfficeEditing`).
- Declaring by label happens when a label is applied or changed and on the maintenance run, for documents that are published, checked in and not checked out. A checked out document is never declared, so nobody's working copy is dropped (`conflict.records.checked_out` when done by hand).
- Certificates use `ReadableCodes` (Primitives, new), the same alphabet as learning certificates and HR letters; those two keep their own copy for now.
- The digest covers fields that never change after disposal (IDs, number, versions, mode, approver, time), not the label names, which are a copy kept for reading.
- Gap: verify is for records managers inside the tenant only; no public verify page, because a disposition certificate is an internal compliance record.

## Spec 011 R3 slice 2: customer-managed keys per library (T029)

- Defaults are in the spec 011 R3 plan. Migration 0019 of Documents adds `customer_key` and `customer_key_at` to libraries and `encrypted` to document versions (set by the background pass).
- `DocumentStorage` wraps the tenant object storage for document files: keys `documents/{library:N}/...` of a library on the key are sealed on write and opened on read, through a scratch file that is deleted on close, so downloads keep a length and range reads. Only those keys are ever checked for the sealed format. Assets and import archives keep the plain storage.
- Format: 8 byte magic, data key id, 8 byte nonce prefix, then 64 KiB chunks with AES-256-GCM; the nonce carries the chunk index and a last chunk flag, and the header is the associated data. No new library.
- `LibraryEncryption.RunAsync` runs in the document maintenance worker (50 versions per tenant per minute) and stops at the first key that does not answer.
- Gaps: extracted text (`document_version.text_extract`), the search index and the AI index stay under platform encryption at rest; the tenant key protects files only. Background jobs of a revoked tenant that open a library file fail and retry later.

## Spec 035 R3 slice 1: sender-constrained tokens with DPoP (T021)

- Defaults are in the spec 035 R3 plan. Migration 0009 of PublicApi adds `require_dpop` to apps and `publicapi.dpop_proof`, the used proof IDs (hashed with the key thumbprint), cleared by the hourly maintenance run.
- `DpopProofs` checks a proof with the same key rules as private key JWT (`ClientKeys`) and the identity model library already used; no new library. The token endpoint checks it in `TokenRequestCheck`; `/api/public` and `/mcp` check it in `BearerTokens`, recording the proof ID in a scope of its own so it is spent even when the call fails.
- OpenIddict keeps the `cnf` claim for its own certificate binding, so `cnf.jkt` is added to the access token principal after OpenIddict prepares it, and the refresh token keeps the thumbprint in the private claim `tb_jkt`. The token response type is set to `DPoP` when a proof was given.
- Gaps: no `DPoP-Nonce` from the server and no `dpop_jkt` binding of the authorization code at the authorization endpoint (the code is bound when it is redeemed); mutual TLS is not offered.

## Spec 035 R3 slice 2: MCP prompts (T022)

- `McpPrompts` holds the catalogue and the `prompts/list` and `prompts/get` handlers of the MCP server; texts are API catalogue keys `mcp.prompt.<name>.*` in English and Arabic.
- A prompt is offered when the tool gateway offers every tool it names to the caller, the same call the tool list makes, so nothing new decides what a client sees.
- Gap: the catalogue is fixed in code; tenant-defined prompts would be versioned configuration (constitution XXIX) and are not in R3.

## Spec 035 R3 slice 3: admin APIs beside SCIM (T023)

- The R3 release line asks for admin APIs, and FR-012 forbids public operations that change roles and the other administration areas. The slice keeps FR-012: role administration is read only, through scope `roles:read` (delegated, admin consent, capability `api.admin`, Entitlements migration 0046). Writes wait for a decision on FR-012.
- The operations live in the Authorization module (`AuthorizationPublicEndpoints`) and call `AuthorizationAdmin`, so the person's `authz.roles_view` coverage decides the answer, as in the admin area.
- `PublicApiDeveloperTests` keeps guarding FR-012; it now allows administration paths that only read.
- Gap: no public reads yet for security policy, identity providers, entitlements or apps; they stay in the admin area.

## Spec 035 R3 slice 4: developer sandbox tenants (T024)

- Defaults are in the spec 035 R3 plan. Migration 0005 of Tenancy adds `tenancy.tenant.sandbox_until`. The lower limits are entitlement overrides that end on that date, written through `ISubscriptionProvisioning.ApplySandboxLimitsAsync`, so Tenancy never touches Entitlements tables.
- `SandboxWorker` runs hourly in Workers (and in the development host when `Tenancy:Sandboxes:InProcess` is set), suspends due sandboxes and closes them after 14 days through the same status change the platform page uses.
- The developer page reads the end date through `ITenantSettingsReader.GetSandboxUntilAsync`.
- Gaps: no self-service sandbox sign-up; production sandboxes start without demo data.

## Spec 035 R3 slice 5: public app gallery (T025)

- Defaults are in the spec 035 R3 plan. Migration 0010 of PublicApi adds `publicapi.gallery_app` (platform data, no tenant column) and `publicapi.app.gallery`; a gallery registration reads its name, publisher, description, scopes and redirect URIs from the gallery, as verified clients read theirs from the verified list, and shows as blocked once the entry is withdrawn.
- `AppGallery` adds an app through `Apps.AddFromGalleryAsync` (package and app limit checks) and then `Consents.GrantAdminAsync`, so the admin consent is the same record an administrator would give by hand. Audit actions `api.gallery_app_changed` (platform) and `api.gallery_app_added` (tenant).
- Gaps: no publisher self-service and no platform screen for the gallery.

## Spec 035 R3 slice 6: confirmation inside verified AI clients (T026)

- Defaults are in the spec 035 R3 plan. The external AI client policy gains `confirmInClient` (off). Agents migration 0025 adds `agents.external_action.decided_in`.
- The MCP endpoint uses the SDK's multi round-trip requests: it throws `InputRequiredException` with one form elicitation and the action ID as request state, and reads the answer from `inputResponses` on the retry. It decides through the new Agents contract `IExternalActionConfirmations`, which checks employee, app, tool, argument hash and expiry before the same decision path as the TeamBeats card.
- Clients on protocol revisions before `2026-07-28` (no multi round-trip requests) keep the TeamBeats card only; URL mode elicitation is not used.

## Spec 024 R3 slice 1: compensation (T018)

- Defaults are in the spec 024 R3 plan. The graph gains the `compensation` event kind, `Handler`, `ForCompensation` and `ActivityRef`; the reader takes the handler from the association of the boundary.
- The executor keeps `InstanceState.Compensable` (activity and scope, in order of completion) and runs handlers in a scope of kind `compensation` owned by the waiting token. The engine saves the list with the rest of the state.
- Capability `workflow.bpmn_advanced` (Entitlements migration 0047, Enterprise & Government) is checked at publish through `ProcessGraph.UsesAdvanced`.
- The modeler reads and writes compensation: the compensation event on boundary, throw and end events, the handler a boundary names (written as a BPMN association), the activity a throw or end event undoes, and a "Compensation step" switch on tasks.
- Gaps: no multi-instance or sub-process compensation; the canvas does not draw the association line, the properties panel shows it.

## Spec 024 R3 slice 2: instance migration (T019)

- Defaults are in the spec 024 R3 plan. `Graph/InstanceMigration` holds the rules (propose, check, apply) with no database; `ProcessEngine.MigrateInstanceAsync` checks and moves one instance under its row lock, and `ProcessOperations.MigrateAsync` checks the operator and writes the audit event `process.migration_executed`.
- Workflow migration 0020 grants update on `workflow.message_subscription`, so a waiting token keeps its subscription when its step is renamed.
- Gaps: no saved migration plans (each run sends its mapping); no batch beyond 100 instances per call; child instances of call activities are moved on their own process.

## Spec 024 R3 slice 3: BPMN import from other tools (T020)

- Defaults are in the spec 024 R3 plan. `Graph/BpmnImporter` converts the file with the same safe XML settings as the reader (no DTDs, size limit) and then runs `BpmnReader`; only structural reader problems refuse the import, so an unfinished draft (no connector yet) is still saved for the designer.
- Report items use catalogue keys `process.import.*` with the element and a detail (attribute or value). A new key is required: `conflict.process.model_exists`.
- Added the DMN export of a published decision version for US7 scenario 2.
- Gaps: no import of DMN files from other tools; group and user names are not looked up in the directory (only TeamBeats IDs are kept); Activiti and Flowable extensions are listed as unsupported rather than converted.

## Spec 024 R3 slice 4: Camunda 8 user tasks (T021)

- Defaults are in the spec 024 R3 plan. The `camunda8` pack reuses the ticketing pack base (address checks, sign-in, status mapping); the base gained an overridable capability key and a 204 answer for writes with no body.
- Workflow migration 0021 adds `workflow.external_task`. `ExternalTasks` reaches Camunda only through `IConnectorRuntime` and finds connections through `IConnectorActions`; people come from the user directory contract. `ExternalTaskWorker` is hosted by the worker, and in the development host with `Workflow:ExternalTasks:InProcess`.
- New keys: `validation.process.external_task_outcome`, `validation.process.external_task_comment`, `conflict.process.external_task_not_open`; audit action `process.external_task_completed`.
- Gaps: no candidate group tasks; no rendering of Camunda forms; the sync reads up to 2,000 open tasks per connection a minute; Camunda SaaS sign-in that needs an `audience` parameter is not supported by the shared OAuth sign-in yet.

## Spec 027 R3 slice 1: SIEM export of agent events (T021)

- Agent events already were audit events, so they reach SIEM destinations through the spec 026 stream. `AgentRuns.FinishAsync` now writes `agent.run_finished` for every run except evaluation runs; outcome `denied` for limit, policy and suspension stops.
- Default: no agent-only destination filter; receivers select by the `agent.` prefix (documented in `docs/security/siem-schema.md`).

## Spec 027 R3 slice 2: MCP servers as tool sources (T022)

- Defaults are in the spec 027 R3 plan. `ai.agents.mcp` and `ai.agents.autonomous` are new capabilities (Entitlements migration 0048, Enterprise and Government). The `mcp_server` pack (Integrations) speaks Streamable HTTP, protocol version 2025-06-18: each call opens a session with `initialize`, sends one request and reads a JSON or event stream answer. No persistent session and no server-sent notifications.
- `integrations.mcp_tool` (migration 0021) keeps each listed tool, its schema hash and the hash that was approved. `McpToolSource` adds approved tools to the agent tool registry as `mcp.<name>`; a tool of a turned-off connection or one that is not approved is refused with `tool.connector_disabled` or `tool.mcp_not_approved`.
- The admin panel sits on the connection page (Read the tool list, review and approve, reject). Gap: no diff view of what changed in a tool; the administrator sees the current description and status only.

## Spec 027 R3 slice 3: L4 bounded autonomy in processes (T023)

- Defaults are in the spec 027 R3 plan. The process engine (Workflow) runs an agent task like a service task: it checks the package and the dual approval (`workflow.autonomy_approval`, migration 0022), then sends `AgentTaskRequested`; Agents runs the agent in mode `autonomous` as the instance's starter and answers with `AgentTaskFinished`. Neither module reads the other's tables.
- `agents.process_task` (Agents migration 0026) keeps one row per step run, so a redelivered request runs once. Security incidents may now be opened by the platform (`opened_by` null, `kind` set).
- The approval panel sits on the process model page; the modeler edits agent tasks (`tb:agent`).
- Gap: an L4 run keeps its record in one transaction with its answer, so a restart in the middle of a run starts it again from the beginning when the request is redelivered; tools it already called may be called again. Write tools that change other systems should stay idempotent.
- Gap: no service identity for processes started without a person (FR-003 allows one granted by the AI Policy Administrator); such steps raise `agent_no_person`.
- With T024 (pages, `agent-autonomy.spec.ts`) spec 027 R3 is built.

## Spec 020 R3: handoffs and advanced templates (T024 to T026)

- Defaults are in the spec 020 R3 plan. `ai.agents.orchestration` (Workplace Plus and Enterprise and Government) and `ai.agents.orchestration_custom` (Enterprise and Government) are new capabilities (Entitlements migration 0049). Agents migration 0027 adds `run.parent_run_id` and `run.depth`.
- `agent.handoff` is offered only in modes run and test, and only to an agent that declares handoffs. A handoff ends the first run with outcome `handed_off` and starts the target run in the same request, as the same person, with the target's definition, tools and unit policy. The answer carries `handedOffFrom` with the agent asked and the agent that answered at the end of the chain. In the test panel a handoff is recorded as `would_call` and nothing starts.
- Templates 13 to 15 are in `Templates/advanced.json` under `ai.agents.advanced`, with platform suites in the five suite locales. No analytics tool exists yet, so Executive Intelligence uses search, news and pages. Process Automation is not ready without `ai.agents.autonomous`, whatever the agent policy says.
- The editor offers the tenant's published agents as handoff targets (by agent ID); a template target shows the name of the agent installed from it.
- Gaps: the target gets only the request the first agent wrote, not the conversation; handoffs are not offered in L4 process tasks, briefings or evaluation runs; there is no picker for template targets in the editor, since platform templates declare those themselves.

## Spec 034 R3: L4 process runs and estate trends (T026 to T028)

- Defaults are in the spec 034 R3 plan. Evaluation migration 0016 adds `process_step_eval` (one row per L4 step run, counts and checks only) and `process_instance_eval` (instances that had an agent task, closed by `ProcessInstanceCompleted` or `ProcessInstanceWithdrawn`).
- Agents publishes `AgentTaskRunRecorded` in the same transaction as `AgentTaskFinished`, with counts read from its own run steps. Evaluation reads no table of Agents or Workflow. Modeled process keys carry `ProcessKeys.ModelPrefix` in events (now in Workflow.Contracts); the API takes the key without it.
- Estate trends (`GET /api/v1/evaluation/estate/trends`, `ai.evals.advanced`) reuse the dashboard rule for a version's result: its latest completed run, leaving route and comparison runs out. The noise margin is the platform default (2 points), since repeat runs are not kept per version for every agent.
- Pages: Estate trends tab under Evaluation, and the agent task evaluation panel on the process model page. The panel shows only when the person reads evaluations of the process's agents.
- Gaps: L4 steps have deterministic checks only, with no judged answer quality until process datasets exist. An instance outcome that is processed before its step facts is not kept. Estate benchmarks cover the last 12 months.

## Spec 017 R3: Oracle Fusion and SAP packs, marketplace (T022 to T024)

- Defaults are in the spec 017 R3 plan. `finance_oracle_fusion` and `finance_sap_s4` are packs behind the finance hand-off contract, under the spec 005 keys `integration.oracle_fusion` and `integration.sap`; no Entitlements migration was needed. The hand-off picks the oldest enabled connection of any finance kind (`FinancePack.Kinds`).
- Both packs sign in with basic, OAuth client credentials or a bearer token through `RestSignIn`, reach only allowlisted hosts through the SSRF guard, keep claim payloads out of capture, and never return ERP fields beyond the reference and the canonical status.
- A retried submit first looks for the invoice by the claim's number (Oracle `InvoiceNumber`, SAP `SupplierInvoiceIDByInvcgParty` with company code and supplier), so both submits are idempotent.
- SAP writes fetch `x-csrf-token` and the session cookie with a read of the service root; a 403 that asks for a token is retried, not taken as a refused credential.
- Gaps: SAP payment (clearing) is not read; Oracle and SAP field values were taken from the published API references and have not been run against a customer system; tax lines are not sent, only a tax code per line for SAP.

