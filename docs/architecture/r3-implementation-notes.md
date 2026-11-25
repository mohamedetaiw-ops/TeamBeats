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
