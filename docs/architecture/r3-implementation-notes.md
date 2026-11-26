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
- Marketplace: `GET /api/v1/integrations/packs` now also returns each pack's capability key, whether the platform offers it, why not, and the tenant's connection count; the page is `/admin/connections/marketplace`. Integrations migration 0022 adds `platform_pack_state` (platform data). `PUT /api/v1/platform/connector-packs/{kind}` turns a pack off or on, with platform audit. A pack turned off refuses new connections (`conflict.integrations.pack_unavailable`), and its jobs wait with `pack_unavailable` until it is on again, then run.
- Gaps: SAP payment (clearing) is not read; Oracle and SAP field values were taken from the published API references and have not been run against a customer system; tax lines are not sent, only a tax code per line for SAP. The platform switch has no page in a platform console yet; it is an API for the platform operator.

## Spec 021 R3 slice 1: KPI tiles (T022, with spec 017 T024)

- Defaults are in the spec 021 R3 plan. Analytics migration 0006 adds `kpi_tile_set` (versioned documents, forced RLS). `GET /api/v1/analytics/kpi-tiles` and `PUT` (tenant settings managers, with the version read) under `analytics.kpi_tiles`; the location dashboard answer gains `kpis`, null for units and without the capability.
- Analytics now references Integrations.Contracts and reads readings only through `IKpiFeed`. `KpiReading` gained `Source`, the connection's display name, which `KpiFeed` fills in when reading.
- The admin page is `/admin/analytics/kpi-tiles`; the permission picker offers the permissions the workspace's roles grant.
- Gaps: a location dashboard e2e with readings is not possible yet, since locations come only from directory sync; the integration suite covers the tiles on the dashboard. Tiles show the latest value only, without a trend.


## Spec 021 R3 slice 2: usefulness of personalization and recommendations (T023)

- Event `recommendation.outcome` (nonessential, user keyed) with `surface` in `search`, `assistant`, `learning` and `outcome` in `shown`, `opened`, `dismissed`. `SearchService` sends `shown` when a page holds a result ranked from use; `AssistantSuggestions` sends `shown` when it offers questions and `dismissed` when the person turns them off; Learning `Recommendations` sends `shown` and `dismissed`. The search results page, the assistant panel and the recommended courses send `opened`. The suggestions and recommendations GET routes now commit so the outbox event goes out.
- `GET /api/v1/analytics/experience` gains `recommendations`: people shown something per surface and, at or above the minimum group size, how many opened or dismissed and the opened rate. Shown on the experience dashboard as "Personalization and recommendations".
- Defaults recorded in the plan: judged samples stay the spec 034 search sets (unpersonalized); quick navigation is not a surface; no unit or location breakdown.

## Spec 021 R3 slice 3: governed experience experiments (T024)

- Experience module: `Personalization.Experiments`, tables `experience.experiment` (one open per page, unique partial index) and `experience.experiment_consent`, migration 0023. Routes `GET/POST /api/v1/experience/experiments`, `POST .../{id}/approve`, `POST .../{id}/stop`, `GET/PUT /api/v1/me/experiments`. Capability `analytics.experiments` (Enterprise and Government, already in the catalogue).
- `PageService.RenderAsync` asks `Experiments.ArmAsync` for the viewer: running experiment, package, consent, unit scope, not a kiosk; the arm is `SHA-256(experiment:person) % 100 < percent`. The variant shows its release; `experiment.exposure` (server only) is sent and the render route now commits so the outbox event goes out.
- Analytics module: `ExperimentResults` at `GET /api/v1/analytics/experiments/{id}` counts people per arm from exposures and, after first exposure, `task.outcome` complete and `content.viewed`, with the minimum group size. Analytics reads no Experience table.
- Web: Analytics > Experiments (`/analytics/experiments`) to propose, approve, stop and read results; "Take part in page experiments" on the preferences page. Tests: `ExperimentTests` (2), `experiments.spec.ts` (en and ar, axe).
## Spec 019 R3 slice 1: Oracle HCM and SAP SuccessFactors packs (T015)
- The named packs are presets, not new adapters: the stored settings are HR REST settings, so every HR path (sync, live read, payslips, leave, data change) keeps one implementation. The preset decides the paths and paging; a tenant may override single fields. Recorded default: the first entry of each Oracle list (names, emails, work relationships, assignments, managers) is used, as Oracle lists the primary first; a suspended assignment counts as active.
- SuccessFactors returns active users only unless asked, so the preset filters on `status in 't','f','T','F'` to see leavers (FR-020). Its `/Date(ms)/` values are read by `HrMapping` for every HR pack.
- The generic REST pack gained offset paging and list addresses with a fixed query (characters limited, percent encoded); the record ID stays escaped wherever it is placed.

## Spec 019 R3 slices 2 and 3: compensation statement and salary letters (T016 to T018)
- Compensation is a live read like payslips and comes with `hr.payslips` (recorded default: section 9 lists no separate key). Kiosks block `/compensation` because the shared device allow list never names it.
- Letters read compensation only at print time through `IHrCompensation`, so approvers see no salary field and nothing is stored; a letter that cannot be printed keeps a reason key (`hr.letter.issue_problem`) shown to HR, and the existing worker retries it up to 20 times.
- Recorded defaults: the basic salary is the component with the connection's basic salary code; the total needs one currency and one frequency; amounts print with two decimals and grouping, in Arabic-Indic digits when the tenant uses them. Oracle HCM has no compensation preset because salaries there follow each tenant's salary basis.

## Spec 018 R3 slice 1: answers to invitations (T019)
- Answers are their own connection feature (`calendar_responses`, delegated `Calendars.ReadWrite`), so a tenant that only reads its calendars never grants write access. Recorded defaults: a note of up to 500 characters, the organizer told unless the employee unticks it, and only invitations (not responded, tentative or accepted) answerable; the employee's own meetings are not.
- Graph answers `202 Accepted` with no body; the delegated gateway now treats it like `204`. A `400 ErrorInvalidRequest` (answering a meeting one organized) maps to `rejected`, shown as `refused`.
- The read cache moves the employee's calendar to a new generation after an answer instead of removing keys, so My calendar and the unified calendar read fresh at once without a refresh limit.

## Spec 018 R3 slice 2: Teams presence on profiles (T020)
- Presence is its own connection feature (`presence`, delegated `Presence.Read.All`) and is read as the viewer, so Teams' own privacy decides; the visibility check is the free and busy one, now shared by both endpoints. Graph values are folded into six availabilities and four activities; the status message never leaves the adapter.
- Recorded defaults: kept a minute per viewer; the profile reads it in the browser so a slow Teams answer never holds the page; unknown availability and every non-ok state show nothing; one's own profile does not ask.

## Spec 018 R3 slice 3: mail summaries by the assistant (T021)
- The tenant switch is a Mail feature option, `assistantSummaries`, so turning it on asks for `Mail.Read` through the usual consent and drops older tokens. The employee's opt-in is one row in `integrations.mail_summary_optin` (no mail is ever stored), audited both ways, refused while the tenant does not offer it, and deleted on `UserDeprovisioned`.
- `mail.inbox` reads as the employee through the same delegated token, Inbox only, newest first, at most 25 messages, with the preview as plain text and never the full body. Its result is Confidential, so the runtime's classification cap (spec 027 FR-017) and the gateway's Confidential routing (spec 013) apply; message text reaches the model in the untrusted envelope like any tool output.
- Recorded defaults: 10 messages unless the agent asks for up to 25; an optional unread-only filter; the opt-in shows in Outlook settings only while the tenant offers it or the employee is still opted in, so they can always take it back.

## Spec 018 R3 slice 4: Google Workspace mail through the mail port (T022)
- `IMailbox` is now a composite: Outlook while Microsoft 365 is in the plan and its Mail feature is on, otherwise Gmail while the tenant's Google Workspace connection has its new `mail` feature on. Every answer carries `source` (`outlook` or `google`), so My mail shows the same states for both and says which app its links open.
- Gmail is read as the employee's own address through the connection's domain-wide delegation with `gmail.metadata` only, so Google gives labels and the From and Subject headers and never a body or preview. Focused is Gmail's Primary category; `IMPORTANT` maps to high importance, `UNREAD` to unread and `multipart/mixed` to an attachment marker. Nothing is stored; answers are cached in memory 2 minutes per tenant and user, with the last good answer for an hour shown as degraded during an outage.
- A person whose address is outside the Google domain gets `no_mailbox` and no token is asked for. The connection test checks the mail delegation with the administrator's address.
- The home offers `google.my_mail` (capability `integration.connectors`, connection feature `google_workspace:mail`) and renders it with the Outlook My mail placement. `IConnectionFeatures` answers Google features from the pack settings, and the component registry allows an underscore in the connection kind.
- Calendar: Google Calendar already sits behind the meeting calendar port (spec 037 FR-012), and My calendar placements render the spec 037 calendar card, so Google meetings show there with no new code. Recorded defaults: colleague free and busy, rooms, invitation answers and assistant mail summaries stay Microsoft 365 only until a spec asks for Google versions.

## Spec 013 R3 slice 1: tenant provider keys (T031, T032)
- `Ai:TenantProviderKeys` is an install setting, off by default, so shared installs never offer it; there a save is refused with `conflict.ai.provider_keys_not_offered` and any row left behind is ignored.
- `ai.provider_key` holds one secret manager reference per tenant for OpenRouter. A new or changed reference is untested; calls use the key only after `POST /api/v1/ai/provider-keys/openrouter/test` passed. The test reads OpenRouter's own key record (`GET /key`), which costs nothing.
- The gateway reads the key once per request scope and passes it in the provider target; the OpenRouter adapter keeps its own address. A tested tenant key makes OpenRouter reachable for that tenant even when the install has no platform key, but never where egress is blocked.
- `ai.usage_record.key_owner` says who paid: `tenant` for the tenant's own key or a private endpoint, else `platform`.
- The section sits on the Private endpoints page, beside the other tenant-owned model access. Audited as `ai.provider_key_set`, `ai.provider_key_tested` and `ai.provider_key_removed`; the key is never returned or logged.

## Spec 013 R3 slice 2: air-gapped model hosting (T033, T034)
- `Ai:AirGapped` makes the OpenRouter adapter unreachable whatever its key, and a tenant's own key cannot change that. The catalogue page leaves unreachable providers off as before.
- The install's own OpenAI-compatible server is the `local` provider (`LocalModelProvider` in `AiGateway.OpenAiCompatible`), configured by `Ai:Local:BaseUrl` and `Ai:Local:ApiKey`. Its address is operator configuration, so its client may reach private addresses; tenant endpoints keep the `SafeHttp` rules. The platform operator lists its models as `local/{model}`; only `local` models may have a Restricted ceiling (a database check enforces it too).
- The gateway treats `local` like a private endpoint for Restricted content and the sovereign rule (`ModelCatalogue.InOwnNetwork`). Hosted models are metered like any model, with `key_owner = platform`.
- The policy view carries `airGapped`, shown as a note on the Model policy page, and a customer-hosted install that is air-gapped shows `install_ai_air_gapped` as a passed posture check.
- Recorded defaults: one hosted server per install; hosted models are shared by every tenant of the install, each choosing them through its own policy.

## Spec 022 R3 slice 7: receipt reading in every package (T025)
- Entitlements migration 0050 turns `ai.receipt_reading` on for Workplace. Receipts still need `hr.expenses`, so a Workplace tenant reads receipts once it has expenses through an add-on or an override. Each read goes through the AI Gateway and counts against the AI quota as before.

## Spec 022 R3 slice 8: probation and contract milestones (T026, T027)
- Spec 019 gains the canonical HR fields `probation_end_date` and `contract_end_date` (mapping, `HrEmployee`, stored record). Capabilities `hr.milestones` and `hr.rosters` are added in Entitlements migration 0051 (off for Workplace, on for Workplace Plus and above).
- Hr migration 0014 adds `hr.milestone` (one row per person and kind, source `hr` or `manual`, status `open`, `reviewed` or `closed`, and once-per-date markers for the alert, the review and the reminder) and `hr.milestone_review` (outcome, new date, comment, write-back state).
- The hourly HR job and every `HrSyncCompleted` refresh dates from HR and send what is due. A moved date reopens the milestone and clears the markers, so everything is sent again for the new date. A date HR holds cannot be set by hand (`conflict.hr.milestone_from_hr`).
- Reviewer: the line manager. When the person has none, the HR officers covering them review (smallest safe default, the spec names only the manager). The review is a personal task `probation_review` in the inbox. A review opened inside the last seven days sends no separate reminder.
- Outcomes: confirm and end close the milestone. Extend moves a manual date at once; for a date from HR the milestone waits as `reviewed` until HR brings the new date. The outcome and new date go to HR as a spec 019 data change of group `probation`; the comment stays in TeamBeats and is never audited. Without a connection that takes changes the review records `none`.
- Endpoints under `/api/v1/hr/milestones`; pages `/hr/milestones` and `/hr/milestones/{id}`.

## Spec 022 R3 slice 9: shift rosters (T028, T029)
- Hr migration 0015 adds `hr.shift_pattern` (names per language, start and end on the same day, break minutes, a badge tone, active), `hr.roster_week` (one per manager and week start, `draft` or `published`, with the last published copy as JSON) and `hr.roster_entry`.
- Patterns are kept by HR officers of the whole organization (`hr.manage` at tenant scope) and turned off rather than deleted. A manager plans only their direct reports, inside the seven days from the week start; weeks of one manager may not overlap. The planner's default week starts on Monday (smallest safe default; any date may start a week).
- Saving replaces the week's entries; publishing stores the copy that attendance reads. The first publish tells everyone planned; a later one tells only the people whose days differ from the last published copy, and refuses when nothing changed. HR officers covering the manager open a week with `managerId` and see it read only.
- Attendance asks `ShiftRoster` for published shifts: a shift replaces the working week and the rule's hours for its day (the break comes off the expected hours), and a day off on the roster is a day off. Without `hr.rosters` nothing changes. Shifts that cross midnight are not offered yet.
- Endpoints `/api/v1/hr/shift-patterns`, `/api/v1/hr/rosters/{weekStart}` (`:publish`) and `/api/v1/me/shifts`; pages `/hr/rosters` (planner and patterns) and My shifts on `/attendance`.

## Spec 023 R3: rooms, mirroring, summaries and sentiment (T016 to T021)
- Capabilities `engagement.rooms` and `engagement.mirroring` (Entitlements migration 0052), off for Workplace. Engagement migrations 0012 (rooms, messages, reports), 0013 (mirrors and sent posts) and 0014 (`engagement.ai_setting`).
- Rooms: owners and engagement administrators open up to 20 active rooms per community; messages follow the comment rules (keyword hold, three reports hide, removal with a reason, mentions through the `engagement.room.mention` template). The live stream is server sent events at `/rooms/{id}/stream`, waking on an in-process signal and polling every 2 seconds, closing after 2 minutes so the browser reconnects and access is checked again.
- Mirroring: one way, set by engagement administrators on active open internal communities without a unit or space. Each new published non anonymous post goes once per mirror (`engagement.mirror_post`); Slack through the spec 017 adapter, Teams through the bot's channel conversation. A refusal is kept as the mirror's last failure. A Teams channel is identified by its id.
- Summaries: `POST /posts/{id}:summarize` (at least 10 visible comments) and `POST /rooms/{id}:summarize` (last 200 visible messages, at least 20), use case `summarize`, the community's classification, the discussion as JSON data inside a tagged block. Nothing is stored. Post and room views carry `canSummarize`.
- AI on anonymous content: `GET/PUT /settings/ai` with If-Match `"a{version}"`, engagement administrators only, audited as `engagement_ai_setting_changed`; shown on the moderation page.
- Sentiment: Analytics migration 0007 adds `topic_key` to `analytics.sentiment`; `AnalyticsText.TopicKey` and the `IAnalyticsSentimentTrends` contract give a module the weekly trend of its own key. Engagement sources `community_room` and `speak_up` (anonymous published posts, read only while the AI setting is on, with no author or unit) join community comments, all keyed by the community id. The trend is on its own page `/communities/{id}/sentiment` linked from the community page (the plan said on the community page; a separate page keeps the feed within its content budget), last 12 weeks, moderators and owners only.

## Spec 028 R3: quiz drafts, skill recommendations and classroom sessions (T013 to T017)

- **Quiz drafts.** `POST /api/v1/learning/manage/courses/{id}/quiz-drafts` with the draft revision, an optional quiz lesson key and a count (1 to 20, default 10). The text of page lessons and of controlled document lessons the administrator may read is sent as JSON data through the AI Gateway (use case `draft`, Internal), one language per lesson, preferring the administrator's own, cut at 60,000 characters. The answer must match a JSON schema; each question is checked by the builder's own rules and dropped when it fails. Kept questions carry `aiDraft: true` and new keys, in a new quiz lesson titled "Check what you learned" or the named quiz lesson. Any change to a question in the builder, or Keep, clears the mark; publishing refuses `validation.learning.quiz_unreviewed` while any mark remains. A refusal or outage returns `outcome` `policy_blocked` or `unavailable` and leaves the draft as it was. A course without readable lesson text is refused before the gateway is called. Gap: video and ordinary document lessons have no text to send.
- **Skill recommendations.** The People contract `IPersonSkills` (listed skills, a person's declared skills; null while skills are off) is the only way Learning sees skills. `learning.course.skills` holds up to 10 tags (migration 0006); unknown skills are dropped and, while skills are off, saving keeps the tags as they were. `GET /api/v1/learning/recommendations` returns up to six published, open, targeted courses of TeamBeats (not LMS courses) tagged with a skill declared at basic or good, without courses the learner has open or completed or dismissed, ranked by matching skills, then basic before good, then newest. A dismissal (`POST /recommendations/{courseId}:dismiss`) lasts for good.
- **Classroom sessions.** Lesson kind `classroom` has no source and no quiz. Capability `learning.classroom` (Entitlements 0054, Workplace Plus and above). Sessions (migration 0007) belong to a course and lesson key, so they outlive new versions; a session needs a start in the future, an end within 12 hours, a place or an https meeting link, 1 to 500 seats and an active facilitator. One live seat (booked or waiting) per learner and lesson is enforced by a partial unique index. Booking needs the course open and the lesson not done; a full session puts the learner on the waiting list. Giving a seat back before the start, or adding seats, promotes the waiting list in order with the `learning.session.seat` notice. Cancelling tells everyone booked or waiting (`learning.session.cancelled`). The learning maintenance worker sends `learning.session.reminder` once per booked seat within a day of the start. Attendance (`PUT /sessions/{id}/attendance`) is for the facilitator or an administrator of the course, from the start until 14 days after the end; attended finishes the lesson through the same path as a package completion, so certificates, obligations and `CourseCompleted` follow. An attendance mark changed from attended to absent does not take a completed lesson back.
- **Pages.** The builder shows a quiz draft panel, skill tags while skills are on, and sessions under a saved classroom lesson. The player shows a classroom lesson's sessions with Book, Join the waiting list and Give it back. My learning shows Recommended for your skills and, for facilitators, a link to `/learning/sessions`; `/learning/sessions/{id}` is the roster with attendance.
- **Not built.** Calendar invitations for sessions (spec 037 does not read learning sessions yet), and a learner's own view of past attendance beyond the seat badge.

## Spec 029 R3: timeline, time tracking, Planner and Jira sync, status summaries (T013 to T017)

- **Timeline.** Spaces migration 0006 adds `task.start_on` (on or before the due date), `estimate_minutes` and `finished_before_blocker`, the `task_link` table (blocker in the same space, at most 20 per task, loops refused by a recursive check) and `time_entry`. `GET /api/v1/spaces/{id}/timeline` returns dated tasks, links, milestones and undated tasks; a conflict is a task that starts before a blocker is due. `PUT /tasks/{id}/schedule` is the keyboard way to move a bar. Completing a task with an open blocker is allowed and noted. The chart is the shared `ScheduleChart` (spec 036 DLS-040).
- **Time.** Entries of 1 to 1,440 minutes on a day no later than today and at most 365 days back. Only the author changes an entry; owners remove any. `GET /spaces/{id}/time?from&to` (owners, default last 30 days, at most 366) and `time.csv`.
- **Status summaries.** `POST /spaces/{id}/status-summary` needs `ai.assistant` and sends the newest 300 tasks, the milestones and the last 10 status notes of this space only, as quoted JSON, through the AI Gateway (use case `summarize`, Internal, or Confidential for a Confidential space so the tenant policy decides). Nothing is stored; `spaces.status_summarized` is audited with the outcome.
- **Planner and Jira sync.** Capability `spaces.task_sync` (Entitlements 0055, Workplace Plus and above). Two new spec 017 packs, `tasks_planner` (Microsoft Graph, client credentials or bearer, names the Microsoft 365 group in `groupId`) and `tasks_jira` (Jira Cloud REST v3, basic or bearer), behind the `ITaskTrackers` contract; only canonical task fields cross it. Spaces migration 0007 keeps links (one active per list and per plan or project), task pairs with the versions both sides had, and 90 days of history. Runs happen every 10 minutes from the spaces maintenance worker, in their own transaction per tenant, or on an owner's request, never inside other requests. The first run pairs tasks with the same title and creates the rest on both sides. A pair whose side changed is carried to the other; when both changed the later change wins and a conflict is kept. Tracker tasks come in as made by the owner who linked the list, and synced changes notify nobody. Jira keeps descriptions; Planner keeps them in task details, which are not synced. Deleting on either side ends that pair and deletes nothing. A failed or refused change stays changed and goes with the next run. Updates carry a key per run, so a refused update is sent again; a create carries a key per task version, so a timeout never makes a second issue, and a refused create is sent again once the task changes.
- **Defaults.** Planner gives no modified time, so a Planner change is dated when it is seen and wins a conflict against an earlier space edit. Assignees outside the space are left out and named in the history by work email. Jira has one assignee: the first space assignee Jira knows is sent.
- **Pages.** The task page has its plan and time widgets, the space has Timeline and, for owners, Time and Sync. Connections offer Microsoft Planner and Jira Software in the wizard.
- **Not built.** Planner task details (description and checklist), Jira custom fields, and Asana.

## Spec 031 R3 slice 1: floor plans (T011, T012)
- A floor plan is one PNG, JPEG or WebP image per floor, up to 5 MB and 8000 pixels a side, checked by its signature, scanned for malware and kept in tenant object storage. SVG is refused because it can carry script. Each resource is placed as a point in percent of the image, so the plan scales with the screen and works RTL without mirroring the image.
- The floor map shows each placed resource as free, busy or unavailable for the chosen time; the list beside it is the full alternative with the same actions, and resources not yet placed show only there.
- Administrators place resources in the plan editor at `/booking/manage/floors/{id}`; employees open the map from Find a space at `/booking/floors/{id}`.

## Spec 031 R3 slice 2: visitors and reception (T013, T014)
- A visit belongs to its host and site. Its code is eight characters from `A-Z` and `2-9`, unique per tenant while the visit is open, and works at reception only on the visit day in the site's time zone. Recorded defaults: at most 20 visits per host per day, 15 minutes to 12 hours long, at most 90 days ahead.
- The visitor is emailed through the external email port with templates `booking.visit.registered`, `changed` and `cancelled`, retried up to 5 times, then shown to the host as not sent so they pass the code on. The email links to the tenant home (`/`), since visitors have no account.
- Reception is the `booking.reception` permission, held by the facilities administrator role. Checking in tells the host (`booking.visitor_arrived`). A leaver's open visits are cancelled with reason `left_company`. Visitor names, addresses and companies are removed 30 days after the visit; the visit counts stay for reports.
- Pages: Visitors at `/booking/visits` (from My bookings) and Reception at `/booking/reception` (from Booking).

## Spec 031 R3 slice 3: occupancy sensors (T015)
- Sensors come through the `occupancy_rest` connector pack (capability `booking.sensors`) with one action, `list_sensors`, a `GET /sensors` answering an array, or an object with `sensors` or `items`, of entries with `id` or `sensor_id`, `occupied` or `state`, and an optional `observed_at`. Only the sensor id, occupied flag and time leave the adapter, up to 5000 sensors.
- Administrators map a connection's sensors to the site's resources on the site page. Each connection is read at most every 5 minutes by the booking maintenance job. Only hourly counts per resource (`booking.occupancy_hour`: reads and occupied reads) are kept, never raw readings, so no person can be traced from them.
- Sensor check-in is a booking setting, off by default: when on, an occupied reading inside a booking's check-in window checks it in with method `sensor`. Booking settings have no page yet, so it is set through the settings API.
- Usage reports show sensed hours beside booked hours and the site's visit counts.

### Evidence
- US7: `BookingFloorPlanTests`, `booking-floor-plans.spec.ts`.
- US8: `BookingVisitTests` (limits, emails, codes on the day only, changes, leavers, purge, cross-tenant and permission denial), `booking-visitors.spec.ts`.
- US9: `BookingSensorTests` (mapping validation, five minute reads, sensor check-in on and off, sensed hours, cross-tenant and entitlement denial).

## Spec 037 R3 slice 1: registration, reminders and attachments (T018 to T020)
- An event takes either RSVP or registration, not both. Registration has seats (1 to 10000), an optional waiting list and an optional closing time; the closing time applies to every occurrence of a series, as the spec is silent on per occurrence closing. Places are taken under a lock on the event row, so two people never get the last seat. When someone gives up a seat or the seats grow, the first person waiting is promoted and told (`calendar.registration_promoted`); when seats shrink below the people registered the edit is refused.
- The calendar file is an iCalendar file for one occurrence at `/api/v1/calendar/events/{id}/calendar-file?occurrence=`, with UID `{event}@teambeats`, so importing it twice updates the same entry.
- Reminders (none, 15 minutes, 1 hour, 1 day) and cancellation notices go through spec 015 in the new `events` category, emailed by default. The calendar maintenance job runs every minute per tenant in the Workers host; each notice is recorded in `calendar.reminder_sent` before it is sent, so a retry never repeats one, and a place taken after the reminder time gets no reminder.
- Up to 5 attachments per event of at most 20 MB: PDF, PNG and JPEG by signature, Word, Excel and PowerPoint as a zip with the matching extension. Each is scanned and kept under `calendar/events/{event}/{file}` in tenant object storage, uploaded as the raw body with `?name=`, read only by the event's audience and its managers, and removed with the event.

## Spec 037 R3 slice 2: shifts and holiday status (T021, T022)
- The My shifts layer shows published roster entries (shifts and days off) from Hr through `IHrCalendarFeed.ShiftsAsync`, for the viewer and their reports only, looking back 28 days. Drafts are never shown. The layer is offered only with `hr.rosters` and only to someone with a published roster, and it is on by default only for someone with no saved layer preferences.
- Team availability takes working days from the published roster where there is one, and from the work calendar otherwise.
- Hr holidays gain `provisional` (shown as tentative until confirmed) and `observedFor` (the date the holiday falls on when it is moved, which must differ and be within 7 days). Both still count as holidays for working days.
- Pages: registration, the calendar file and attachments in the event details; answers, seats, waiting list, closing time, reminder and files on the manage page; the provisional and observed columns on the holiday calendar page.
- CAL-032 is not built in this slice.

### Evidence
- US8 to US10: `CalendarRegistrationTests` (seats under concurrency, waiting list promotion, closing, calendar file, reminders once and not for late places, cancellation notices, attachment type, size, malware, limit and audience checks, cross-tenant denial), `calendar-registration.spec.ts`.
- US11: `CalendarShiftTests` (published only, entitlement and roster gating, reports only, roster working days, provisional and observed holidays).

## Spec 010 R3: skill suggestions and Teams presence (T017 to T020)
- Teams presence on the profile was delivered with spec 018 R3: `GET /api/v1/people/{id}/presence` uses the free and busy visibility rule (FR-014) and the profile shows it in words.
- Skill suggestions are off for each person until they turn them on at `/people/me`, and a run happens only when they press Suggest skills, at most three times a day. Turning them off deletes open suggestions; dismissed ones are kept so they are not offered again.
- A run reads the person's newest 50 items of the last 12 months through `IAuthoredWorkSource` in People.Contracts, implemented by Content (published items whose published version the person wrote, not Confidential or Restricted, title and summary or the opening of the body) and Engagement (the person's published, named posts in active communities that are not Confidential). Documents and knowledge articles are not read yet.
- The items and the tenant's active skill list go to the AI Gateway as quoted data with the `expertise` JSON schema. Only ids from the list that the person has not declared or dismissed are kept, at most five, each with the items it came from (source, id, title, link). The text read is never stored.
- Requires `people.skills` with skills on and `ai.assistant`. Audit: `people.skill_suggestions_changed` and `people.skills_suggested` (outcome, items read, skill ids).

### Evidence
- US9: `SkillSuggestionTests` (consent off reads nothing, own named non-Confidential items only, citations, accept and dismiss, day limit, other person and other tenant not found, off deletes open ones, Workplace has none), `people-suggestions.spec.ts`.
- US10: spec 018 R3 presence evidence.

## Spec 025 R3: change impact and regulator feeds (T024 to T027)

- Change impact (US9, FR-022, FR-023): `GET /api/v1/policies/{id}/impact` gives the facts for the version in review against the version in force to the people who may compare versions (editors, managers, route reviewers): changed clauses, attestations of the version in force, approved exemptions in effect, check questions on changed clauses, linked controls, related policies the reader may see, and the onboarding flag. No person is named.
- `POST /api/v1/policies/{id}/impact/assessment` sends the changed clauses, the controls (key `framework:code` with title) and the related policies (id and name) to the AI Gateway as JSON data under the policy's classification. Anything in the answer that was not in those lists is dropped. The assessment is kept in `policies.impact_assessment` per version and locale with the hash of what it read, and every request is audited (`policy.impact_assessed`) with its outcome. A confidential policy keeps its facts and gets `policy_blocked`.
- Regulator feeds (US10, FR-024, FR-025): capability `policy.regulator_feeds`, Enterprise and Government only. Connector pack `regulator_feed` (spec 017, REST sign-in, tenant allowlist) with one read `list_changes`: `GET {address}` answering a JSON Feed or a plain list. At most 200 items; only id, title, https link, date and summary leave the pack, as plain text with tags and control characters removed, the summary cut to 2,000 characters. Contract `IRegulatorFeeds` in Integrations.Contracts.
- `PolicyMaintenanceWorker` reads each enabled feed when its last read is 6 hours old or more (`policies.regulator_feed_read`), keeps new items in `policies.regulator_change` (unique per connection and item id) and tells tenant holders of `policy.manage` once per read that found new items (`policies.regulator_changes_new`). Items are deleted 2 years after they were read, and items published more than 2 years ago are not kept.
- `GET /api/v1/policies/regulator-changes?status=new|dismissed|actioned`, `POST .../{id}:dismiss` and `POST .../{id}:link` (policy ids, start reviews, note) for people with `policy.manage`. A started review is a `policy_review` due in 30 days with `regulator_change_id`, told to the owner like a scheduled review; a policy with an open review keeps it. Decisions are audited as `policy.regulator_change_decided`. The reviews page names the change.
- Defaults recorded: feeds are JSON only for now (RSS and Atom wait for an XML reader in spec 017); a started review is due in 30 days; notices go to compliance officers at tenant scope; a decided item cannot be decided again.
- Pages: the review page shows "What the change touches" under the change summary; `/policies/regulator-changes` is linked from the policies navigation for people who manage policies when the package has the feature. The connection wizard offers "Regulator change feed".
- Tests: `PolicyImpactTests` (facts, kept assessment, dropped unknown controls and policies, confidential, employee and other tenant), `RegulatorChangeTests` (six hour reads, five fields, https only, one notice per read, dismiss, link and review start, open review kept, purge, package off, other tenant), `policy-impact.spec.ts` (en and ar, axe). The e2e stack has no regulator feed to read, so the item list is covered by the integration tests and the e2e checks the page with no feed connected.

## Spec 009 R3: guest sites, campaign automation and event items (T038 to T043)

- Guest sites (US10, FR-025): capability `content.guest_sites`, Enterprise and Government only. `content.site_guest_share` keeps the site, the space, the expiry (1 to 365 days, 30 by default), who shared it and when it was revoked. People who manage the site share it into a space they own (`ISpaceAccess`); an archived site cannot be shared and stops being read at once. `GET/POST /api/v1/content/sites/{id}/guest-shares`, `POST /api/v1/content/site-guest-shares/{id}/revoke`. Readers of the space use `GET /api/v1/content/shared-with-space/{spaceId}` and its `sites/{siteId}/items` and `items/{itemId}`: published Public and Internal items only, as text, without comments, reactions or confirmation. Audited as `site.shared_with_guests` and `site.guest_share_revoked`.
- Campaign automation (US11, FR-026, FR-027): capability `content.campaign_automation`, Enterprise and Government only; without it `follows`, `delayDays` and `goal` are refused with `validation.content.campaign_automation`. A step with `follows` (the 1-based place of an earlier step in the request) and `delayDays` (1 to 60) is planned at the earlier step's planned time plus the delay; when the earlier step is sent the follow-up's time is set to that send plus the delay. It goes to the people the earlier step reached who have not acted on its item (confirmed it when it asks for confirmation, otherwise opened it) and who may still read the follow-up's item. A follow-up of a skipped or cancelled step is skipped.
- A goal (`goal: { itemId, percent }`) is an item of the campaign's steps and 1 to 100 percent. The content scheduler checks the goal of running campaigns before sending due steps: the share of the current audience that acted on the goal item. When reached, pending steps are skipped, the campaign completes with `goal_reached_at`, the people who publish at the site are told (`content.campaign.goal_reached`, document update category) and it is audited as `campaign.goal_reached` with the counts. The dashboard shows the share, hidden below the minimum group size. Saving new steps without a goal clears it; a change without steps keeps it.
- Event items (FR-018 R3): content type `event`. `content.item_calendar_event` keeps only the link from an item to a company event; the calendar keeps the event. Contract `ICalendarEventCards` in Calendar.Contracts (`PublishedAsync`, `ForReaderAsync`, `UpcomingAsync` behind `GET /api/v1/content/items/event-choices`): the card has the titles, status, next occurrence (or the last one when all are over), place, online link and the reader's own registration, checked against the event's audience on every read. Creating an event item needs a published event of the tenant (`validation.content.event`); other types refuse an event (`validation.content.event_type`). The reading page shows the card beside the text and links to the calendar, where people register; readers outside the event's audience read the text without the card. A cancelled event shows as cancelled while the calendar shows it.
- Defaults recorded: a follow-up's audience is the earlier step's recipients at send time; the goal counts acted people over the audience at the time of the check; guests never read Confidential items of a shared site; there is no guest feed, search or notice of new items; an event item picks from the company and department events of the coming year that the author sees.
- Tests: `GuestSiteTests`, `ContentCampaignTests` (follow-ups after the delay, skipped follow-up, goal reached, refused without the feature and checked with it, goal kept and cleared), `ContentEventItemTests` (card, registration from the calendar, outside the audience, cancelled, other tenant), `content-guest-sites.spec.ts`, `content-campaign-automation.spec.ts` and `content-event-items.spec.ts` (en and ar, axe).

## Spec 008 R3: behavioral personalization (T027, T028)

- Capability `experience.behavioral_personalization` (Enterprise and Government). `experience.behavior_policy` per tenant: enabled (off by default) and consent mode `opt_in` or `opt_out`, read and changed with `GET/PUT /api/v1/experience/behavior-policy` by holders of `personalization.policy` at tenant scope, audited as `experience.behavioral_personalization_changed` with the mode before and after. Turning it on without the feature is refused (`validation.experience.behavior_unavailable`).
- `GET/PUT /api/v1/me/personalization/behavior` gives each person the policy, their choice and whether it applies (`experience.behavior_consent`). Declining deletes their signals at once; favorites, recents, layouts and preferences stay.
- `experience.behavior_signal`: daily counts per person and page address, written beside each recent visit only while personalization applies to the person, purged after 90 days by `PageScheduleWorker`, and included in the preference export.
- Quick navigation orders matches by the person's own use in the last 90 days and marks those moved up as ranked from their use. Contract `IBehaviorSignals` (Experience.Contracts: `ActiveAsync`, `UsageAsync(kind)`) is for specs 012, 014 and 021.
- Defaults recorded: one signal kind (pages); no model; a policy change applies at once; turning the policy off keeps signals until they age out but nothing reads them.
- Pages: "Personalization from use" on the shell settings page where the package has the feature; the choice on the preferences page only when the tenant turned it on. Tests: `BehaviorPersonalizationTests` (3), `behavior-personalization.spec.ts` (en and ar, axe).

## Spec 012 R3: personalized ranking (T017, T018)

- `SearchService.SearchAsync` takes `personalize`, set only by the in-app `GET /api/v1/search`. It reads the person's page use from `IBehaviorSignals` (spec 008), which is empty unless personalization applies to them, looks up each candidate's link in `search.unit`, and fuses the candidates they opened, most used first, as a third reciprocal rank list. Best bets go first after that and the permission check runs on the same candidates, so items, totals and facets do not change.
- Results ranked from use carry `personalized: true` and the results page marks them "Ranked from your use". The public API, agent tools and saved search alerts call without personalization; judged query sets run as the system and never have signals.
- Defaults recorded: no capability key of its own (follows `experience.behavioral_personalization`); one signal kind (pages opened); every candidate the person opened is marked, not only those that moved. Tests: `SearchPersonalizationTests` (2), `search-personalization.spec.ts` (en and ar, axe).

## Spec 014 R3: proactive suggestions (T018, T019)

- `GET /api/v1/assistant/suggestions?path=` (Agents module, `AssistantSuggestions`) offers up to three questions about the item a page shows. Recognised pages: `/content/read/{id}`, `/documents/{id}`, `/policies/{id}` (policies also need `ai.policy_qa`). The item is checked through `ISearchRetrieval.ItemAsync` as the person, so an unreadable item, one the AI ceiling withholds, or an unknown one gives an empty list. The response holds keys and the item title; the panel words the question and sends it as a first turn with the item's scope, which now accepts `content` and `documents` as well as `policies`.
- Capability `ai.assistant.suggestions` (Workplace Plus, Enterprise and Government). The tenant switch is `suggestionsEnabled` on the assistant settings (on by default, audited `assistant.suggestions_changed`); the person's switch is `agents.assistant_preference` via `PUT /api/v1/assistant/suggestions/preference`, shown on the preferences page and as "Don't suggest questions" in the panel.
- Defaults recorded: rules, no model call and nothing stored for a suggestion; only the assistant shows them, never notifications; three item page kinds; two questions per kind. Tests: `AssistantSuggestionTests` (3), `assistant-suggestions.spec.ts` (en and ar, axe); `PolicyQuestionTests` now uses an unsupported source for the bad scope case.
