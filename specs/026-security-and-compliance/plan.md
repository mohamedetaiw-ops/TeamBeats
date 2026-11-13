# Implementation Plan: Security Controls, Compliance and Certifications

**Spec:** `specs/026-security-and-compliance/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** `docs/architecture/r0-solution-structure.md`, ADR-0005 (tenant-scoped identity), ADR-0007 (session store), ADR-0013, spec 003 plan (sessions and Entra sign-in), spec 004 plan (attribute conditions)

## Summary
R1 builds the tenant security policy and the basic alerts: User Story 1 (network rules), User Story 2 (MFA assurance, step-up, session rules), User Story 5 scenarios 1, 2 and 4 within the package split of section 9, the security posture page (FR-017), and secure rendering checks (FR-025). Guests, SIEM, data detection, compliance projects and data residency are planned for R2 in the section "R2 plan" below; customer keys and four-eyes are R3. The certification roadmap is a company program; R1 adds only the product evidence it needs (the trust documents list in the admin shell links to files the platform operator uploads).

R1 slices, in order:
1. **Security policy store and enforcement:** IP rules and session rules, enforced in the request pipeline before any module code.
2. **MFA assurance and step-up:** claim mapping per identity provider, refusal without MFA, step-up flow and the sensitive action list.
3. **Alerts and posture:** download volume and out-of-hours admin grants, alert handling, posture page.
4. **Admin pages:** Security policy, Alerts, Posture, in English and Arabic, RTL.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation | Yes | Policy and alerts tenant-owned with forced RLS; enforcement reads the policy of the tenant resolved from the host and session |
| III authorization | Yes | `security.manage`, `security.view`, `security.respond` through `IAuthorizationService`; per-role network rules are spec 004 conditions, so the explainer shows them |
| V identity | Yes | No product-held credentials; MFA stays with the identity provider; step-up is a fresh OIDC sign-in with `max_age` and `prompt=login` |
| XXI entitlements | Yes | Section 9 capability keys checked through `IEntitlementService` |
| Rule 9 secrets | Yes | Audit records before and after values, never secrets |

## Module
The `Security` module of the module map, with `Security.Contracts`. Session enforcement stays in `Identity` (it owns sessions); it reads the policy through `ISecurityPolicy` from `Security.Contracts`.

## Data model (schema `security`)
- `security_policy`: one row per tenant, versioned: `ip_allow cidr[]`, `ip_block cidr[]`, `admin_ip_allow cidr[]`, `network_zones jsonb` (name to CIDR list), `require_mfa`, `step_up_minutes` (default 10), `idle_minutes` (30), `absolute_hours` (12), `max_sessions` (null for no limit), `remember_device_days`, `alert_settings jsonb`, `version`, timestamps.
- `mfa_claim_mapping` per identity provider config: which `amr` or `acr` values count as MFA (defaults for Entra ID: `amr` contains `mfa`, or `acr` names a multi-factor policy).
- `security_alert`: `tenant_id`, `id`, `kind`, `subject_user_id`, `facts jsonb` (counts and references, no content), `status` (open, handled, dismissed), `handled_by`, `note`, timestamps.
- `identity.user_session` gains `last_seen_at` and `authenticated_at` (spec 003 table, migration in `Identity`).

## Enforcement
- **Client address:** only `RemoteIpAddress` after the forwarded headers middleware, which already trusts only configured proxies (`Http:TrustForwardedHeaders`, FR-002). Client-set headers are never read.
- **IP rules (US1):** a middleware right after tenant resolution: blocklist first, then tenant allowlist, then the admin allowlist for `/api/v1/admin`, `/api/v1/authz`, `/api/v1/security` and the other admin route groups, marked by an endpoint metadata `AdminArea`. Refusal is 403 `security.network_not_allowed` with no session created; audit `security.request_blocked_network` with the source address, rate-limited per address to avoid audit floods.
- **Network zones (US1 scenario 5):** a new condition attribute `network_zone` for spec 004 conditions, resolved per request from the zones in the policy.
- **Self lock-out (US1 scenario 3):** saving rules that exclude the caller's address needs step-up and a typed confirmation field; break-glass (spec 001) is the recovery.
- **Session rules (US2 scenario 4):** the session middleware ends a session idle longer than `idle_minutes` or older than `absolute_hours`; `last_seen_at` is written at most once a minute. Concurrent limit: the oldest session is revoked on a new sign-in. `POST /api/v1/me/sessions/revoke-all` and the admin action for a user revoke all sessions (`SessionRevoked`), within 1 minute.
- **MFA assurance (US2 scenario 1):** at Entra sign-in the ID token's `amr` and `acr` are checked with the mapping; when `require_mfa` is on and MFA is not shown, no session is created and `identity.sign_in_refused` is audited with reason `mfa_missing`. The default rule of section 6 applies: `require_mfa` turns on only after a test sign-in shows the claim.
- **Step-up (US2 scenarios 2 and 3):** sensitive endpoints carry `RequireStepUp` metadata. When `now - authenticated_at > step_up_minutes`, the API answers 401 `security.step_up_required` with a step-up URL; the web shell sends the user to the identity provider with `prompt=login` and `max_age=0`, and the callback refreshes `authenticated_at` only if `auth_time` is fresh. The default list of section 6 is mapped to existing endpoints (role assignment, identity configuration, security policy, audit export, connection credentials); R2 items attach as they are built. Dev sign-in (dev host only) is treated as fresh.

## Alerts (US5, R1 part)
- Download volume: the `Documents` module (spec 011) publishes `DocumentDownloaded` (IDs only); `Security` counts per user per hour and raises `download_volume` above the tenant threshold (default 200 files or 2 GB). Built when spec 011 downloads exist; until then the rule is wired and tested with the event.
- Out-of-hours admin grant (`security.alerts_advanced`): on `RoleAssignmentChanged` for a role holding any admin permission, outside the tenant working week and hours (tenant default calendar of spec 022, hours 08:00 to 18:00 by default), raise `privilege_grant_out_of_hours`.
- Blocked network sign-ins: counted from the network refusal path.
- Handling: `POST /api/v1/security/alerts/{id}/handle {outcome, note}` audited as `security.alert_handled`. Notification to security officers through spec 015 when it exists; until then the alert list and an admin shell badge.

## Posture (FR-017)
`GET /api/v1/security/posture` lists checks with state and a link: MFA required, IP rules set, idle and absolute timeouts at or below the recommended values, admin area rule set, step-up window at or below 10 minutes, Entra sign-in configured, dev sign-in absent (production artifact). No score formula in the spec, so the page shows passed checks out of total.

## Secure rendering (FR-025)
Already partly built for pages (spec 032 FR-035). R1 adds an architecture or e2e test that the web app's content security policy has no `unsafe-inline` for scripts, and a test that rich text fields go through the server sanitizer.

## API (v1)
- `GET/PUT /api/v1/security/policy` (`If-Match`, step-up), `GET /api/v1/security/policy/preview?ipAllow=` (users who signed in from outside the range in the last 30 days, a count).
- `GET /api/v1/security/alerts`, `POST /api/v1/security/alerts/{id}/handle`.
- `GET /api/v1/security/posture`.
- `POST /api/v1/me/sessions/revoke-all`, `POST /api/v1/identity/users/{id}/sessions/revoke-all`.

## Tests
- Integration: US1 scenarios 1 to 5 (allowed, blocked, admin area only, self lock-out confirmation, forwarded header spoofing ignored, network zone condition with explainer), US2 scenarios 1 to 4 (MFA claim present and missing, step-up required and satisfied, idle and absolute timeout with a fake clock, revoke all), US5 scenarios 1, 2 and 4.
- Cross-tenant: tenant A's policy never applies to tenant B; alerts of A not visible in B; policy endpoints reject cross-tenant IDs.
- Entitlements: scoped IP rules and advanced alerts refused on Workplace.
- Audit: every policy change with before and after values; no secrets.
- Acceptance (e2e): edit policy with step-up and preview, handle an alert, posture page, in English and Arabic with axe.

## R2 plan

R2 adds User Story 4 (SIEM), the rest of User Story 3 (guests), User Story 5 in full, User Story 6 (sensitive data detection), User Story 7 (compliance projects), FR-008 (data residency) and FR-020 (break-glass visible to security officers). Guest spaces themselves are built in spec 029: invitations, sign-in through the tenant's provider, expiry, the Restricted and Confidential file rules. This plan adds what spec 026 owns on top. FR-019 (deletion certificate respecting legal hold) waits for legal hold in spec 011 R2.

R2 slices, in order:
1. **SIEM streaming (US4, FR-014, FR-018):** destinations, delivery, buffering and replay, test event, admin page.
2. **Guests (US3, FR-009 to FR-012):** guest domain rules, `security.max_guest_spaces`, the reduced guest shell, watermarked downloads, access reviews for guests and admin role holders.
3. **Alerts in full (US5 scenarios 3 and 5, FR-013):** guest access to Confidential items, app and AI client alerts (denied calls, call volume), tenant thresholds.
4. **Sensitive data detection (US6, FR-015):** built-in and tenant patterns, warn and block, pattern test.
5. **Compliance projects (US7, FR-021 to FR-024):** control library, projects, evidence tasks, readiness view, sharing to auditor spaces.
6. **Residency and platform access (FR-008, FR-020):** region at tenant creation, break-glass sessions in the tenant's audit view.

### SIEM streaming
- **Source:** the tenant's audit stream (spec 006), which carries every security event because each one is audited (`security.*`, `auth.*`, `guest.*`, `api.*` refusals and writes). The audit sealer gives every event a gapless `chain_pos` within 5 seconds, so the stream reads sealed events in `chain_pos` order through a new `IAuditStream` contract in `Audit.Contracts` (Security never reads audit tables). Spec 035 call log rows (one per API call, reads included) are usage telemetry, not audit: they stay on the usage page and are not streamed.
- **Buffering and replay (US4 scenario 2):** each destination keeps a cursor (`last_chain_pos`). Nothing is copied into a buffer table: audit keeps events for at least 365 days, so a destination that was down for 7 days or more resends from its cursor in order. Every message carries the audit event ID, so a receiver can drop a duplicate after an unclear failure (at least once delivery).
- **Destinations (US4 scenario 1):** HTTPS JSON, signed like spec 017 webhooks, with the secret in the tenant's secret manager; or syslog over TLS (RFC 5425 framing of RFC 5424 messages, the JSON event as the message). Both go through Integrations as an unlisted pack, `siem`, as spec 035 does for event subscriptions: the host allowlist and the SSRF guard apply, and the connection is hidden from the Connections area. At most 2 destinations per tenant.
- **Worker:** every 10 seconds, per tenant with an enabled destination, a batch of up to 500 events goes in one request (HTTPS) or one connection (syslog). On failure the cursor stays, the destination shows the error and the time since its last success, the next attempt waits with backoff (up to 5 minutes), and `security.siem_delivery_failed` is audited at most once an hour. After 24 hours of failures, `security.alerts` notifies the security officers.
- **Schema:** documented in `docs/security/siem-schema.md`: `id`, `tenant`, `time`, `action`, `outcome`, `actor` (type and ID), `target` (type, ID, org unit), `sourceIp`, `correlationId`, `changes`. Changes are already free of secrets (spec 006 `Sensitive`).
- **Test event (US4 scenario 3):** `POST /api/v1/security/siem/{id}:test` sends one `security.siem_test` event outside the stream and shows the result.
- **Secret rotation (edge case):** replacing the secret reference needs step-up. The old secret stops at once because every delivery reads the current reference.
- **Data:** `security.siem_destination` (`tenant_id`, `id`, `kind` https or syslog, `name`, `connection_id`, `enabled`, `last_chain_pos`, `last_success_at`, `failing_since`, `last_error`, `next_attempt_at`, `version`).
- **API:** `GET/POST /api/v1/security/siem`, `PUT/DELETE /api/v1/security/siem/{id}`, `POST /api/v1/security/siem/{id}:test`, `POST /api/v1/security/siem/{id}:enable|disable`. `security.manage`, step-up for create, update and delete. Capability `security.siem`.
- **Start point:** a new destination starts at the current end of the stream, unless the administrator asks for up to 7 days of history.
- **Audit:** `security.siem_configured` (create, change, delete, enable, disable, with before and after values and no secret), `security.siem_delivery_failed`.

### Guests
- **Domain rules:** the tenant guest policy (`security.policy` columns `guest_allow_domains` and `guest_block_domains`) is checked by Spaces through `ISecurityPolicy` before an invitation is sent.
- **Limit:** `security.max_guest_spaces` counts the spaces that have guests or open invitations.
- **Reduced guest shell (section 10):** a guest's session gets an experience with only their spaces, each space's sponsor and end date, and the shared items. The navigation, home, search box and assistant are hidden, and every page outside the spaces refuses them as today.
- **Watermark (US3 scenario 3):** guest downloads of PDF files are stamped with the guest's address and the time through the Gotenberg route already used for letters. Other files download unchanged unless the space turns download off for guests.
- **Access reviews (FR-012):** every 90 days, a review opens for each sponsor (their guests) and for each admin role holder's manager (their admin role assignments). An item not confirmed within 14 days is removed: the guest leaves the space, or the role assignment is revoked. Audit actions `guest.reviewed` and `security.access_review_completed`.

### Alerts in full
- Guest access to Confidential items (US5 scenario 3): Documents publishes `GuestOpenedClassified` when a guest downloads or previews a Confidential item; the tenant setting chooses whether the sponsor, the security officers or both are told.
- App and AI client alerts (US5 scenario 5): the PublicApi module publishes `ApiCallsDenied` when an app passes 50 authorization denials in 10 minutes, and `ApiVolumeSpike` when an app's hour is over 5 times its 30 day hourly average. Security raises `api_denied_burst` and `api_volume_spike` for the security officers and API administrators.
- Tenant thresholds (`security.alerts_advanced`): download files and bytes, denial count, volume factor.

### Sensitive data detection
- **Patterns:** built in for the launch countries: Saudi national ID and Iqama (10 digits with the Luhn check), UAE Emirates ID (784 and 15 digits with the check digit), Egyptian national ID (14 digits with century and governorate rules), Qatar ID (11 digits), IBAN (mod 97), payment card (Luhn, 13 to 19 digits), passport (letter and digit forms, warn only).
- **Tenant patterns:** regular expressions with a timeout, tested against sample text before they are enabled (US6 scenario 3).
- **Hooks:** `ISensitiveDataScanner` in Security.Contracts is called by Content on publish and by Documents on upload and on new versions (text extracted for search). The result is a list of types and locations, never the matched value.
- **Modes per location type** (guest space, audience everyone, other): off, warn or block. A warning is confirmed with a reason, audited as `security.sensitive_data_confirmed`.
- **AI classification suggestions** (section 11) are not built in R2: pattern matching only.

### Compliance projects
- **Module:** a `Compliance` module (data in schema `compliance`), since controls, projects and evidence are a separate area from security policy.
- **Control library:** ISO 27001:2022 Annex A (93 controls), SOC 2 trust services criteria (common criteria CC1 to CC9, plus availability and confidentiality) and NCA ECC (main domains and controls), shipped as versioned platform JSON with control IDs and short titles in English and Arabic. Tenants add their own frameworks.
- **Projects:** a project picks a framework and an org unit scope. Each control gets an owner, a status (not started, in progress, evidence ready, reviewed), a due date, links to policies (025), documents (011) and evidence tasks.
- **Evidence:** an attachment of an audit export (006) keeps the export ID and the SHA-256 of the file.
- **Readiness view:** controls by status, overdue tasks, by owner and org unit.
- **Sharing (FR-024):** a project shared into a guest space shows read-only to its guests.
- **Permissions and package:** `compliance.manage` and `compliance.view`, capability `compliance.projects`.

### Residency and platform access
- **Region:** the tenant has a `region` chosen at creation from the platform's configured regions (`Platform:Regions`, default one region). `deployment.region_choice` allows a choice. The region is shown on the posture page. Storage and database placement per region is an operations concern, recorded in the runbook.
- **Break-glass:** sessions write to the platform stream with a subject tenant (spec 001). The tenant's security officers see them through `IAuditStream`'s platform view filtered to their tenant, on the audit page.

### Tests (R2)
- SIEM: HTTPS delivery signed and in order; syslog over TLS against a local TLS listener; a destination that is down resends from its cursor after recovery with no gaps; test event; secret rotation; cross-tenant streams; package refusal; audit of configuration.
- Guests: domain rules; space limit; reduced shell; watermark; access reviews remove unconfirmed items after 14 days.
- Alerts: Confidential guest access; denied burst; volume spike.
- Detection: each built-in pattern with valid and invalid samples; warn and confirm; block on guest and everyone locations; tenant pattern test; no matched value in logs or audit.
- Compliance: project from a framework, evidence with export hash, readiness counts, guest auditor read-only, org scope, package.
- e2e: SIEM page, guest shell, detection warning, compliance project, in English and Arabic with axe.
