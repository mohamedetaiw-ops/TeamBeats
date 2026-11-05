# Feature Specification: Security Controls, Compliance and Certifications

| Field | Value |
|---|---|
| Spec ID | 026-security-and-compliance |
| Area | platform |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (tenant security policy: IP rules, session rules, MFA assurance check, step-up for sensitive actions; encryption baseline; security alerts; trust documents), R2 (guest users and secure extranet spaces, SIEM streaming, sensitive data detection, compliance projects and control library, data residency choice, ISO 27001:2022 certificate and SOC 2 Type I report in hand by general availability, SOC 2 Type II six months after), R3 (customer-managed encryption keys, four-eyes admin changes, national government certifications, on-prem hardening pack) |
| Last updated | 2026-11-05 |
| Requirement IDs | TEN-009, IAM-009, ADM-002, ADM-003, SEC-001 to SEC-021 and CMP-001 to CMP-004 (new), CX-NFR-010 (shared with spec 032); foundation section 14 (Security, Privacy), 14.2, and open business decision 9 (certifications and residency) |
| Use cases | UC-01, UC-03, UC-14 |
| Depends on | 001 (tenants, break-glass), 003 (identity), 004 (authorization), 005 (entitlements), 006 (audit), 009 and 011 (content and documents shared with guests), 015 (notifications), 021 (security dashboards), 025 (policy to control mapping) |

## 1. Problem and outcome

**Problem:** Enterprise, banking, healthcare and government buyers do not shortlist an intranet without a security questionnaire, certificates and specific controls: IP restrictions, strong authentication, encryption with their own keys, audit logs, data residency, and a safe way to give auditors and contractors limited access. Our specs already cover tenant isolation, SSO, central authorization and audit, but they do not say how a tenant admin sets security policy, how external people get in, what evidence we hand to a customer's auditor, or which certifications we commit to and when.

**Outcome:** Each tenant has a security policy it controls: network rules, session rules, authentication assurance, step-up for sensitive actions, guest access and data handling. External people (auditors, contractors, partners, customers) get guest access to secure spaces with read-only defaults, sponsors and expiry. Security events reach the customer's SIEM. Compliance officers run audit-readiness projects inside TeamBeats against a control library. We hold ISO 27001:2022 and a SOC 2 Type I report for the SaaS service by general availability, a SOC 2 Type II report six months later, and we publish a trust pack that answers most questionnaires without a call.

## 2. Scope

**In scope (product controls):**
- Tenant security policy: IP allowlists and blocklists (tenant-wide, per admin area, per role), session idle and absolute timeouts, concurrent session limits, remember-device period, sign-out everywhere.
- Authentication assurance: check that the identity provider performed MFA (from token claims) and refuse sessions without it when the tenant requires it; step-up re-authentication with a fresh MFA for sensitive actions (for example role grants, security policy changes, exports, guest invitations).
- Encryption: TLS 1.2 or later in transit, encryption at rest for databases, object storage, search and vector indexes and backups, per-tenant data keys, and customer-managed keys in R3.
- Data residency: tenant chooses the hosting region in shared SaaS from the regions we offer; data at rest, backups and indexes stay in that region; AI routing respects the region where the tenant requires it (spec 013).
- Guest users and secure extranet: guest identities, guest spaces with content, documents, policies and evidence packs shared into them, sponsors, expiry, access reviews, read-only by default, watermarking.
- Security alerts for tenant security officers: unusual download volume, many failed authorization checks, sign-ins from blocked networks, privilege grants outside working hours, guest access to Confidential items.
- SIEM streaming of audit and security events (completes the later-release item of spec 006).
- Sensitive data detection: warn authors and uploaders when content contains patterns such as national ID numbers, IBANs, card numbers or passport numbers, with tenant-defined patterns; optional block for public or guest-shared locations.
- Four-eyes approval for critical tenant settings (identity configuration, security policy, retention minimums, key changes) in R3.
- Access reviews: periodic review of admin role holders and guests by their sponsors or managers.
- Compliance projects: a control library (ISO 27001:2022 Annex A, SOC 2 trust services criteria, NCA ECC, and tenant-defined frameworks), assessments with owners and due dates, evidence tasks, links to policies (spec 025), documents (spec 011) and audit exports (spec 006), and a readiness view per framework.
- Tenant-facing security pages in the admin shell: current security posture score, settings that are weaker than recommended, and links to fix them.

**In scope (company program, with product evidence):**
- Certification roadmap in section 6.
- Annual third-party penetration test, with summary letter in the trust pack.
- Vulnerability disclosure policy and security contact.
- Trust pack: certificates, SOC 2 report (under NDA), penetration test letter, completed standard questionnaires (CAIQ, SIG Lite), data processing agreement, sub-processor list, architecture and data flow summary, business continuity summary, accessibility conformance report.
- On-prem hardening guide and shared responsibility matrix for customer-hosted installs.

**Out of scope:**
- MFA methods inside TeamBeats. MFA stays with the customer's identity provider (spec 003 FR-009); this spec checks that it happened.
- AI agent permissions, autonomy and oversight (agent governance spec 027).
- Device management and mobile app protection policies (customer MDM; mobile app spec planned as 030).
- A full governance, risk and compliance (GRC) product. Compliance projects cover audit readiness for the intranet and simple frameworks, and can export to a GRC tool.

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Tenant administrator | Set security policy, regions (at onboarding), guest settings, SIEM connection | `security.manage` at tenant scope, step-up required |
| Security officer | View security alerts and posture, run access reviews, view audit (spec 006), manage detection patterns | `security.view`, `security.respond` |
| Compliance officer | Manage control library, compliance projects, evidence tasks | `compliance.manage` |
| Guest sponsor | Invite guests to spaces they manage, renew or remove them | `guest.invite` on the space |
| Guest | Read what is shared with their guest space | Guest membership |
| Platform operator | Provision regions, rotate platform keys, run break-glass (spec 001) | Platform role, audited |

## 4. User scenarios

### User Story 1: Restrict access by network (Priority: P1, R1)

A bank tenant allows TeamBeats only from its office ranges and VPN, except that the admin area is limited to the security operations network.

**Independent test:** set tenant and admin area IP rules; sign in from allowed and blocked addresses; confirm API and UI refusal and audit.

**Acceptance scenarios:**
1. **Given** a tenant IP allowlist, **When** a user connects from an address outside it, **Then** every request is refused with a "network not allowed" page, sessions are not created, and the attempt is audited with the source address.
2. **Given** an admin-area allowlist narrower than the tenant list, **When** an admin opens administration from an address outside it, **Then** admin pages and admin APIs are refused while the rest of the intranet works.
3. **Given** a tenant admin edits the allowlist, **When** the new list would exclude their current address, **Then** the product warns and requires step-up and typed confirmation; a platform recovery path exists through break-glass (spec 001).
4. **Given** the client address is behind the customer's proxy, **Then** the product uses only forwarded addresses from proxies the platform trusts, never client-set headers.
5. **Given** a role-level rule (for example "Finance approvers only from office networks"), **Then** actions that need that role are refused outside the network while read access continues.

### User Story 2: Require MFA and step-up for sensitive actions (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** the tenant requires MFA, **When** a sign-in token does not show MFA (from `amr` or `acr` claims, mapped per identity provider), **Then** the session is refused with a message to contact IT, and the attempt is audited.
2. **Given** a user performs a sensitive action listed in section 6, **When** their last authentication is older than the step-up window (default 10 minutes), **Then** they are sent to the identity provider for a fresh sign-in with MFA, and the action continues only after return.
3. **Given** the identity provider does not support forced re-authentication, **Then** the tenant admin is warned at setup and sensitive actions use the strictest available option.
4. **Given** session rules (idle 30 minutes, absolute 12 hours by default), **Then** sessions end as configured, and "sign out everywhere" ends all of a user's sessions within 1 minute.

### User Story 3: Give an auditor read-only access (Priority: P1, R2)

The compliance officer creates a guest space "External audit 2027", shares the policy evidence pack, three document folders and the audit export, invites two auditors from the audit firm for 30 days, and removes access at the end automatically.

**Acceptance scenarios:**
1. **Given** guest access is enabled for the tenant, **When** a sponsor invites a guest by email to a space, **Then** the guest signs in through an allowed method (section 6) and lands only in that space.
2. **Given** a guest, **Then** they cannot see the directory, the home feed, search outside the space, the assistant (unless enabled for the space), or any content not shared into the space. The API refuses all other resources.
3. **Given** the space is read-only (default), **Then** the guest can view and, if allowed, download with watermark, but cannot upload, comment or edit.
4. **Given** the expiry date, **When** it passes, **Then** the guest's access ends, sessions are revoked and the sponsor is notified. Renewal needs the sponsor to act.
5. **Given** a quarterly access review, **Then** each sponsor confirms or removes each guest; guests not confirmed in 14 days are removed.
6. **Given** content classified Restricted, **Then** it cannot be shared into any guest space.
7. **Given** contractors who need more than read-only (for example a vendor working on a project), **When** the space allows contribution, **Then** guests can upload and comment only inside that space.

### User Story 4: Stream security events to the SIEM (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a tenant admin configures a SIEM destination (HTTPS JSON with a signing secret, or syslog over TLS), **Then** audit and security events stream within 60 seconds, in a documented schema.
2. **Given** the destination is down, **Then** events are buffered for at least 7 days and resent in order with no duplicates beyond at-least-once with event IDs.
3. **Given** a test button, **Then** a test event is sent and the result shown.

### User Story 5: Security alerts (Priority: P1, R1 basic, R2 full)

**Acceptance scenarios:**
1. **Given** a user downloads more than the tenant's threshold (default 200 files or 2 GB in an hour), **Then** security officers get an alert with the user, volume and libraries, and the alert links to the audit trail.
2. **Given** an admin role is granted outside the tenant's working hours, **Then** an alert is raised.
3. **Given** a guest opens a Confidential item, **Then** the sponsor and security officers can be notified per tenant setting.
4. **Given** a security officer marks an alert as handled, **Then** the outcome and note are audited.

### User Story 6: Detect sensitive data before it is shared (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** detection is on, **When** an author publishes content or uploads a file containing a pattern (for example a Saudi national ID or an IBAN), **Then** they see a warning naming the type of data and the location, and can remove it, raise the classification, or confirm with a reason.
2. **Given** the target is a guest space or a site with audience "everyone", **And** the tenant set "block", **Then** publishing is blocked until the data is removed.
3. **Given** a tenant-defined pattern, **Then** it is tested against sample text before it is enabled.

### User Story 7: Prepare for an audit with a compliance project (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a compliance officer starts a project "ISO 27001 surveillance audit" from the ISO 27001:2022 control set, **Then** each control has an owner, status, due date, linked policies (spec 025), documents (spec 011) and evidence tasks.
2. **Given** an evidence task (for example "export admin role changes for the last 12 months"), **Then** the owner can attach the audit export from spec 006 directly, and the attachment keeps its export reference and hash.
3. **Given** the readiness view, **Then** it shows controls by status (not started, in progress, evidence ready, reviewed) and overdue tasks, by owner and org unit.
4. **Given** the project is shared to an auditor guest space, **Then** auditors see controls, status and evidence read-only.

### User Story 8: Customer-managed keys (Priority: P3, R3)

**Acceptance scenarios:**
1. **Given** an Enterprise and Government tenant, **When** the tenant admin connects a key in their key management service, **Then** the tenant's data keys are wrapped with it, and the product shows the key status.
2. **Given** the customer revokes the key, **Then** the tenant becomes unavailable within 15 minutes with a clear message, and comes back when the key is restored. Data is not deleted.
3. **Given** key rotation, **Then** new data keys are wrapped with the new version without downtime.

### Edge cases and exception flows
- IP rules lock out every admin: break-glass path (spec 001), audited, with customer notification.
- Identity provider sends no MFA claims at all: the tenant cannot turn on "require MFA" until a test sign-in shows the claim.
- A guest's email domain matches the tenant's own domain: refused as a guest; they must be a member.
- A guest is invited to two spaces: one identity, two memberships, each with its own expiry.
- Data residency change after go-live: a platform migration project, not a setting; the request is recorded and handled by operations.
- SIEM secret leaked: tenant rotates it with step-up; old secret stops working at once.

## 5. Functional requirements

- **FR-001 / SEC-001**: Tenants MUST be able to set IP allowlists and blocklists for the whole tenant, for the admin area and per role, enforced on every request in the backend. *(TEN-009)*
- **FR-002 / SEC-002**: Client addresses MUST be taken only from the connection or from proxies the platform trusts.
- **FR-003 / SEC-003**: Tenants MUST be able to set session idle timeout, absolute timeout, concurrent session limit and remember-device period, and users and admins MUST be able to end all sessions. *(IAM-009)*
- **FR-004 / SEC-004**: When the tenant requires MFA, the product MUST refuse sessions whose token does not show MFA, using per-provider claim mapping.
- **FR-005 / SEC-005**: Sensitive actions (section 6) MUST require step-up re-authentication within the step-up window.
- **FR-006 / SEC-006**: All traffic MUST use TLS 1.2 or later; data at rest in databases, object storage, indexes, queues that persist and backups MUST be encrypted; tenant data MUST use per-tenant data keys.
- **FR-007 / SEC-007**: Enterprise and Government tenants MUST be able to use customer-managed keys with revocation and rotation (R3).
- **FR-008 / SEC-008**: Shared SaaS tenants MUST choose a hosting region at creation; data at rest, backups and indexes MUST stay in that region.
- **FR-009 / SEC-009**: The product MUST support guest identities and guest spaces with sponsors, expiry, read-only default, contribution option, watermarking and no access to anything not shared into the space. Guest memberships MUST NOT receive the default Employee role (spec 004).
- **FR-010 / SEC-010**: Guests MUST sign in only through the methods allowed in section 6. No product-held passwords (constitution V).
- **FR-011 / SEC-011**: Restricted content MUST NOT be shareable to guest spaces.
- **FR-012 / SEC-012**: The product MUST run periodic access reviews for admin role holders and guests, with automatic removal when not confirmed.
- **FR-013 / SEC-013**: The product MUST raise the security alerts in US5 with tenant-set thresholds, and record their handling.
- **FR-014 / SEC-014**: The product MUST stream audit and security events to a tenant SIEM over HTTPS JSON or syslog over TLS, with buffering and replay. *(ADM-002)*
- **FR-015 / SEC-015**: Sensitive data detection MUST support built-in patterns for the launch countries and tenant patterns, with warn and block modes per location type.
- **FR-016 / SEC-016**: Critical tenant settings MUST support four-eyes approval when the tenant enables it (R3).
- **FR-017 / SEC-017**: The admin shell MUST show a security posture page listing settings weaker than the recommended baseline.
- **FR-018 / SEC-018**: Every change to security policy, guest settings, keys, SIEM and detection MUST be audited with before and after values (no secrets).
- **FR-019 / SEC-019**: Tenant data export and deletion (UC-14) MUST respect legal hold (spec 011) and produce a deletion certificate listing data categories and dates. *(ADM-003)*
- **FR-020 / SEC-020**: Platform operator access to tenant data MUST only happen through break-glass (spec 001) and MUST be visible to the tenant's security officers in their audit view.
- **FR-021 / CMP-001**: The product MUST provide a control library with ISO 27001:2022 Annex A, SOC 2 trust services criteria and NCA ECC sets, and tenant-defined frameworks.
- **FR-022 / CMP-002**: Compliance projects MUST support controls with owners, status, due dates, links to policies, documents and audit exports, and evidence tasks.
- **FR-023 / CMP-003**: Evidence attachments from product exports MUST keep their export reference and content hash.
- **FR-024 / CMP-004**: Compliance projects and their evidence MUST be shareable read-only to guest auditor spaces.
- **FR-025 / SEC-021**: Every web surface MUST enforce secure rendering: a content security policy without inline script, server-side sanitization of rich text and configuration fields, integrity checks for third-party scripts and fonts, embeds only from the tenant allowlist in sandboxed frames, and no executable content through any administrator or employee configuration. The page composition rules are in spec 032 FR-035. *(CX-NFR-010)*

## 6. Business and validation rules

**Sensitive actions needing step-up (default list):** granting or removing admin roles; changing identity provider settings; changing security policy, IP rules, session rules; inviting or renewing guests; creating SIEM or connector credentials; audit export; evidence pack export; legal hold changes; key changes; tenant data export or deletion.

**Guest sign-in methods (default):** (a) the customer's identity provider business-to-business guest feature (for example Entra ID guests), or (b) federation with the guest's own organization identity provider configured by the tenant admin. [NEEDS CLARIFICATION: allow a one-time email code for guests whose organization has no identity provider?] Default: not until a constitution amendment, because the constitution limits product-side credentials; to be decided before R2.

**Defaults:** MFA required on; step-up window 10 minutes; idle timeout 30 minutes; absolute 12 hours; guest expiry 90 days maximum, 30 days default; access reviews every 90 days for admins and guests.

**Certification and assurance roadmap (company program):**

| Item | Scope | Target |
|---|---|---|
| ISO/IEC 27001:2022 certificate | SaaS service, development and support | Stage 2 audit before R2 general availability |
| SOC 2 Type I, then Type II | SaaS service: security, availability, confidentiality | Type I at R1 pilot end; Type II report covering 6 months by R2 GA plus 6 months |
| ISO/IEC 27701 | Privacy extension to 27001 | After 27001, R3 |
| ISO/IEC 27017 and 27018 | Cloud controls and cloud personal data | With the first 27001 surveillance audit |
| ISO 22301 | Business continuity | R3, if tenders require it |
| CSA STAR Level 1, then Level 2 | Cloud self-assessment, then third-party | Level 1 at R1; Level 2 with SOC 2 Type II |
| Saudi NCA ECC and CCC alignment; SAMA CSF evidence for banks | KSA customers | Gap assessment in R2; CCC for government cloud tenders with a KSA hosting region |
| UAE: Dubai Electronic Security Center (ISR) and Abu Dhabi ADHICS for healthcare | UAE customers | Gap assessment in R2; certification on first contracted demand |
| Qatar NIA | Qatar government customers | On contracted demand |
| Data protection laws: UAE PDPL, Saudi PDPL, Egypt PDPL, Qatar PDPPL, GDPR | All customers | Data processing agreement, records of processing and DPIA templates by R1 |
| HIPAA | US healthcare only | Not planned; revisit only with a US healthcare customer |
| Accessibility conformance report (WCAG 2.2 AA) | Product | Each major release |
| Annual third-party penetration test | Product and SaaS infrastructure | Before R2 GA, then yearly and after major changes |

For customer-hosted installs, certifications cover our development and support processes; the customer's own environment is covered by their certification, guided by the on-prem hardening pack and shared responsibility matrix.

## 7. Tenant and organization scope
- Tenant-owned data: security policy, IP rules, guest identities and spaces, alerts, SIEM settings, detection patterns, compliance projects, evidence.
- Organization scoping: guest spaces can be owned by an org unit; compliance projects can be scoped to a subtree; security policy is tenant-wide, with role-level IP rules.
- Cross-tenant denial cases: a guest of tenant A invited to tenant B gets two separate identities (ADR-0005); SIEM streams carry only one tenant's events; key revocation affects only its tenant.
- Sibling, parent and child boundary cases: a sponsor in "Acme Gulf" cannot share "Acme Egypt" content into their space; a scoped compliance officer sees only their subtree's projects.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Security policy and history | Product | Confidential | Life of tenant | Actor IDs |
| Guest identity and membership | Product, identity from guest IdP | Confidential | 1 year after last access | Yes |
| Security alert | Product | Confidential | 2 years | Yes |
| Compliance project and evidence | Product | Confidential | 7 years | Owner IDs |
| Encryption keys | Key management service | Restricted | Per key policy | No |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `security.ip_rules` (tenant-wide) | on | on | on |
| `security.ip_rules_scoped` (admin area and per role) | off | on | on |
| `security.session_policy` and MFA assurance | on | on | on |
| `security.alerts` (download volume, blocked network, alert handling) | on | on | on |
| `security.alerts_advanced` (privilege grants out of hours, guest access to Confidential items, tenant thresholds) | off | on | on |
| `security.max_guest_spaces` (limit; 0 turns guest access off) | 0 | 5 | unlimited |
| `security.siem` | off | on | on |
| `security.data_detection` | off | on | on |
| `security.customer_keys` | off | off | on |
| `security.four_eyes` | off | off | on |
| `compliance.projects` | off | off | on |
| `deployment.region_choice` | off (platform default region) | on | on |

Security baselines (encryption, TLS, audit, MFA assurance) are never turned off by package.

## 10. UX, localization and accessibility
- Security pages explain each setting in plain language with the recommended value and the risk of weakening it.
- Lock-out risks (IP rules, MFA) show a preview: "these N users signed in from outside this range in the last 30 days".
- Guest experience is a reduced shell: space name, sponsor, expiry date, shared items only.
- All texts in Arabic and English in R1.

## 11. AI behavior and data policy
- Sensitive data detection uses pattern matching first; optional AI classification suggestions go through the AI Gateway and never send Restricted content externally.
- AI features are off for guests unless the space enables the assistant, and then retrieval is limited to the space.

## 12. Integrations and events
- Publishes: `SecurityPolicyChanged`, `GuestInvited`, `GuestExpired`, `SecurityAlertRaised`, `KeyStatusChanged`.
- Consumes: audit stream (spec 006), identity events (spec 003).
- External: SIEM endpoints, customer key management services, guest identity providers.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| security.policy_changed / ip_rules_changed / session_rules_changed | audit | setting, before, after, actor |
| security.request_blocked_network | audit | user if known, source address, area |
| security.mfa_missing / step_up_completed / step_up_failed | audit | user, action |
| guest.invited / renewed / removed / expired / reviewed | audit | guest, space, sponsor |
| security.alert_raised / handled | audit | type, subject, handler, outcome |
| security.siem_configured / delivery_failed | audit | destination type, actor |
| security.key_connected / rotated / revoked | audit | key reference, actor |
| compliance.project_created / control_status_changed / evidence_attached | audit | project, control, actor |

## 14. Security and abuse cases
- Spoofed client address headers to pass IP rules: only trusted proxies.
- Guest enumerates tenant users: guests have no directory access and no people search.
- Sponsor invites a personal address to leak content: guest domain allowlist and blocklist per tenant, alerts on Confidential access, watermark.
- Admin weakens security then hides it: every change audited, streamed to SIEM, and shown on the posture page.
- Key revocation used as a denial of service by an insider: key changes need step-up and four-eyes when enabled.

## 15. Non-functional requirements
- IP rule and session checks add under 2 ms p95 per request.
- Session revocation within 1 minute of sign out everywhere, within 5 minutes of role change (spec 003 default).
- SIEM delivery within 60 seconds p95.

## 16. Success criteria
- **SC-001**: Our trust pack answers at least 80% of a standard enterprise security questionnaire without custom work.
- **SC-002**: ISO 27001:2022 certificate and SOC 2 Type I report in hand before the first paid R2 contract.
- **SC-003**: A compliance officer gives an auditor read-only access to evidence in under 10 minutes, with automatic removal at expiry.
- **SC-004**: Every sensitive action in section 6 has a passing test proving step-up is required.

## 17. Open questions
- [NEEDS CLARIFICATION: one-time email code for guests without an identity provider?] Default: no, until a constitution amendment.
- [NEEDS CLARIFICATION: which hosting regions at launch?] Default: UAE and KSA, plus EU for non-GCC customers. Ties to foundation open decision 9.
- [NEEDS CLARIFICATION: is HIPAA needed for any target customer?] Default: no.
- [NEEDS CLARIFICATION: which national certification first: NCA CCC (KSA) or Dubai ISR (UAE)?] Default: the one the first government contract requires.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| TEN-009 | FR-001, FR-003 |
| CX-NFR-010 | FR-025 (with spec 032 FR-035) |
| IAM-009 | FR-003, US2 |
| ADM-002 | FR-014, FR-018 |
| ADM-003 | FR-019 |
| Foundation section 14 Security and Privacy | FR-006, FR-008, FR-015 |
| Foundation 14.2 security engineering baseline | Section 6 roadmap (penetration testing), constitution checks |
| Foundation open decision 9 | Section 6 roadmap, open questions |
| UC-14 | FR-019 |

## Review checklist
- [x] No implementation details
- [x] Every FR is testable and traces to a requirement ID or is marked new
- [x] Cross-tenant and org boundary cases are listed
- [x] Entitlement behavior is defined
- [x] Audit events are defined
- [ ] No open [NEEDS CLARIFICATION] items that change scope
