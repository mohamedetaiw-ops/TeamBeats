# Feature Specification: Policy Management

| Field | Value |
|---|---|
| Spec ID | 025-policy-management |
| Area | knowledge |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (policy register, authoring, approval, publishing, attestation campaigns, reminders, compliance view, policy Q&A), R2 (scheduled review cycles, comprehension checks, exemptions, control and regulation mapping, evidence pack export, manager dashboards), R3 (policy change impact analysis with AI, regulator change feeds) |
| Last updated | 2026-11-14 |
| Approved | 2026-11-05 by Mohamed |
| Requirement IDs | EXP-004, KNO-001, KNO-007, POL-001 to POL-018 (new) |
| Use cases | UC-04 (distribution), UC-06 (policy questions) |
| Depends on | 004, 005, 006, 007, 009 (pages, acknowledgement records), 010 (reporting lines), 011 (controlled documents), 012 (search), 014 (assistant), 015 (notifications), 016 (task inbox), 021 (analytics), 024 (process engine), 026 (compliance controls and evidence) |

## 1. Problem and outcome

**Problem:** Every organization has policies (code of conduct, information security, anti-bribery, leave, travel, health and safety), and regulated ones must prove that each employee read and accepted the version in force. Today policies are PDFs on a share, sent by email, with sign-off tracked in spreadsheets. Reviews are missed, nobody knows which version someone accepted, and employees cannot find the answer to a simple question such as "can I accept a gift worth 300 dirhams?".

**Outcome:** TeamBeats keeps one policy register with an owner, a version history, an approval trail and a review date for every policy. Policies are published as readable pages in every enabled language, distributed to the right people, and accepted through attestation campaigns with deadlines, reminders and manager escalation. Compliance officers see who has accepted what at any moment and export an evidence pack for auditors. Employees ask questions about a policy and get answers that quote and cite the exact clause.

## 2. Scope

**In scope:**
- Policy register: policy ID, title, category, owner, approver group, audience, classification, current version, effective date, next review date, status, related policies, linked controls and regulations.
- Policy lifecycle: Draft, In review, Approved, Published (effective), Under review, Superseded, Retired.
- Authoring as a structured page (spec 009 page engine) with numbered sections and clauses, or as a controlled document (spec 011) for policies that must stay as a signed PDF, with the text extracted for reading and Q&A.
- Approval routes on the process engine (spec 024) with tasks in the task inbox (spec 016), including legal and compliance review steps.
- Distribution: publish to an audience, add to new joiners' onboarding (spec 022), show in a "My policies" page.
- Attestation campaigns: who must accept, by when, attestation text, reminders, manager escalation, re-attestation on material change and on a fixed cycle (for example yearly).
- Comprehension checks: short questions that must be passed before attesting.
- Exemptions: request, approve and track exceptions to a policy for a person or unit, with expiry.
- Compliance view: completion by org unit, location, manager and person, overdue lists, and evidence export.
- Policy Q&A: the assistant answers questions about policies the user can read, quoting clauses with citations; a "Ask about this policy" entry on each policy page.
- Mapping of policies to controls and regulations (for example ISO 27001:2022 Annex A controls, national data protection laws) for audit readiness in spec 026.
- Scheduled reviews: review tasks before the review date, and the outcome (no change, minor update, major revision, retire) recorded.

**Out of scope:**
- General content approval and critical announcements (spec 009). This spec reuses its acknowledgement record.
- Controlled document storage details (spec 011).
- Learning courses and formal training (planned spec 028). A policy can link to a course; completion of a course is not an attestation.
- Whistleblowing and case management for policy breaches (Speak Up in spec 023 covers anonymous reports).

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Read policies in their audience, attest, take comprehension checks, ask questions, request an exemption | CanView on policy |
| Policy owner | Draft and revise, answer review tasks, launch attestation for their policy, see its compliance | Named on the policy; `policy.author` |
| Policy approver | Approve in their route step | Assigned by the route |
| Compliance officer | Manage the register, categories, attestation campaigns, exemptions, control mapping, evidence export | `policy.manage` at tenant or org scope |
| Line manager | See their reports' attestation status and send reminders | Reporting line (spec 010) |
| Auditor (guest, spec 026) | Read-only access to the register, versions, approval trail and evidence pack shared with them | Guest share |

## 4. User scenarios

### User Story 1: Publish a policy through approval (Priority: P1, R1)

Sara, the Information Security manager, revises the Acceptable Use Policy. Legal and the CISO review, the CEO approves, and the policy becomes effective on the 1st of the month in Arabic and English.

**Why this priority:** without a register and approval trail there is nothing to attest to and nothing to show an auditor.

**Independent test:** create a policy, run the route, publish with an effective date; confirm the register, version and audit trail.

**Acceptance scenarios:**
1. **Given** a policy in Draft, **When** the owner submits it, **Then** the configured route (for example Legal, then CISO, then CEO) runs on spec 024 and each approver gets a task with a compare view against the version in force.
2. **Given** an approver returns the draft with comments on specific clauses, **Then** the comments show next to those clauses for the owner.
3. **Given** final approval, **Then** the version gets a number, an effective date, approver names and dates, and the previous version becomes Superseded on the effective date.
4. **Given** a policy with Arabic and English versions and a "legally binding language" set to Arabic, **Then** the English page shows "in case of difference the Arabic text prevails", and both variants must be approved together.
5. **Given** a reader opens a superseded version from an old link, **Then** a banner points to the current version.

### User Story 2: Attestation campaign (Priority: P1, R1)

Compliance launches the yearly Code of Conduct attestation for all employees in the UAE and KSA, due in 30 days. Each person reads the policy and confirms "I have read, understood and will comply". Managers see their team's status. Two weeks before the deadline compliance sees 70% done and sends a reminder to the rest.

**Independent test:** launch a campaign to a test audience, attest as several users, check reminders, escalation and the completion view.

**Acceptance scenarios:**
1. **Given** a campaign for a policy version with an audience and deadline, **When** it launches, **Then** every person in the audience gets a task in the task inbox and a notification, and the policy appears as "action needed" on their My policies page.
2. **Given** a user attests, **Then** the record holds user, policy, version, attestation text, time, channel, comprehension result if any, and a hash of the text shown. The record is immutable.
3. **Given** the deadline is near (configurable, default 7 and 2 days before), **Then** users who have not attested get reminders; **Given** the deadline passes, **Then** their line managers get a list and the status becomes Overdue.
4. **Given** a person joins the audience after launch (new hire, transfer), **Then** they get the task with a deadline counted from the day they joined (default 14 days).
5. **Given** a person leaves the audience before attesting, **Then** they are removed from the open list, and their record shows "left audience" with the date.
6. **Given** a policy version is replaced by a material revision during a campaign, **Then** the compliance officer chooses to close the campaign and start a new one, or keep it for the old version. Attestations always name the version they were given for.
7. **Given** a contractor membership type excluded by the campaign settings, **Then** contractors are not included.

### User Story 3: Know who has accepted what (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a compliance officer opens a policy, **Then** they see completion for the current version as a percentage and counts, by org unit, location and manager, with drill-down to people.
2. **Given** a person's profile in the compliance view, **Then** it lists every policy that applies to them with the last version attested and date.
3. **Given** an org-scoped compliance officer for "Acme Gulf", **Then** they see only people in their scope.
4. **Given** a line manager, **Then** they see only their direct and indirect reports and can send a reminder, but cannot see comprehension answers.

### User Story 4: Ask a question about a policy (Priority: P1, R1)

Ahmed asks the assistant "Can I accept a gift from a supplier?". The assistant answers from the Gifts and Hospitality policy that applies to him, quotes clause 4.2 and links to it.

**Acceptance scenarios:**
1. **Given** a question about a policy topic, **When** the assistant answers, **Then** the answer cites the policy title, version and clause, and links to the clause anchor on the policy page.
2. **Given** two policies apply (group policy and a subsidiary policy), **Then** the answer names both and says which applies to the user's org unit; if the policies conflict, it says so and suggests contacting the policy owner, without picking one.
3. **Given** no policy covers the question, **Then** the assistant says it found no policy that answers it and offers the owner contact for the closest category. It does not answer from general knowledge as if it were policy.
4. **Given** the user opens "Ask about this policy" on a policy page, **Then** answers are limited to that policy and its linked documents.
5. **Given** a superseded version, **Then** it is never used for answers unless the user asks about a past date explicitly.
6. **Given** a policy the user cannot read (for example a restricted HR investigations policy), **Then** it is never used, quoted or named in the answer.

### User Story 5: Scheduled review (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a policy with a 12-month review cycle, **When** the review window opens (default 60 days before), **Then** the owner gets a review task with options: no change, minor update, major revision, retire.
2. **Given** "no change", **Then** the review is recorded with the reviewer and date, the next review date moves forward, and no new attestation is triggered.
3. **Given** "major revision", **Then** a new draft starts and the approval route runs; when published, the policy owner is asked whether to launch re-attestation.
4. **Given** the review date passes with no outcome, **Then** the register shows Overdue for review, and the compliance officer and the owner's manager are notified.

### User Story 6: Comprehension check (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a campaign with a 5-question check and a pass mark of 80%, **When** a user scores below the mark, **Then** they see which sections to reread (not the correct answers) and can try again up to the set number of attempts.
2. **Given** a user fails all attempts, **Then** the policy owner is notified and the user's status is "needs follow-up".
3. **Given** a screen reader user, **Then** the check is fully keyboard and screen reader accessible.

### User Story 7: Exemptions (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** an employee requests an exemption to the remote access policy with a reason and end date, **Then** a request goes through the exemption route (owner, then compliance) in the task inbox.
2. **Given** approval, **Then** the exemption is in the register with scope, reason, approver and expiry, and shows on the person's compliance view.
3. **Given** an exemption expires, **Then** the person and policy owner are notified 14 days before, and on expiry it closes automatically.

### User Story 8: Evidence for auditors (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a compliance officer selects policies and a date range, **When** they export an evidence pack, **Then** the pack has, per policy: versions in force with effective dates, approval trail, review history, attestation completion by unit and the list of attestations, open exemptions, and linked controls, as PDF and CSV, generated in the background and audited.
2. **Given** an auditor guest space (spec 026), **Then** the compliance officer can share the pack read-only with an expiry date.
3. **Given** a control from the control library (spec 026) such as "A.5.10 Acceptable use of information", **Then** the policies mapped to it and their status are listed.

### Edge cases and exception flows
- A policy applies to an org unit that is deactivated: campaigns exclude it and the register flags the audience as needing update.
- A user on long leave during a campaign: compliance can pause their deadline with a reason.
- A policy owner leaves: ownership moves to the compliance officer of the scope until reassigned.
- Attestation by a delegate (spec 016 delegation) is not allowed: attestation is personal. The task does not move to delegates.
- A campaign to 100,000 people: tasks created in the background, progress shown, no page slowdown.
- Mobile attestation: allowed, recorded with channel "mobile".
- Policy text contains instructions that try to steer the assistant: treated as untrusted content (constitution XI).

## 5. Functional requirements

- **FR-001 / POL-001**: The system MUST keep a policy register per tenant with the fields in section 2, filterable and exportable by users with `policy.manage`.
- **FR-002 / POL-002**: Policies MUST follow the lifecycle in section 2 with every transition audited, and MUST keep every published version read-only with approval trail. *(EXP-004)*
- **FR-003 / POL-003**: Policies MUST be authored as structured pages with numbered, linkable clauses, or as controlled documents with extracted text. *(KNO-001)*
- **FR-004 / POL-004**: Approval routes MUST run on spec 024 with tasks in spec 016 and clause-level comments. Owners MUST NOT approve their own policy version.
- **FR-005 / POL-005**: Multilingual policies MUST support a legally binding language, and all variants of a version MUST be approved together.
- **FR-006 / POL-006**: Attestation campaigns MUST support audience, deadline, attestation text, reminders, manager escalation, late joiners, leavers and exclusion by membership type.
- **FR-007 / POL-007**: Each attestation MUST be an immutable record with user, policy, version, attestation text, time, channel, comprehension result and a hash of the text shown. Attestation MUST NOT be delegated.
- **FR-008 / POL-008**: Policies MUST support re-attestation on material change and on a fixed cycle.
- **FR-009 / POL-009**: The compliance view MUST show completion by unit, location, manager and person, respecting the viewer's scope, and a per-person view of applicable policies.
- **FR-010 / POL-010**: The assistant (spec 014) MUST answer policy questions only from policies and versions the user may read, with citations to clause level, and MUST say when no policy covers the question. *(KNO-007)*
- **FR-011 / POL-011**: Each policy page MUST offer a policy-scoped Q&A limited to that policy and its linked documents.
- **FR-012 / POL-012**: Scheduled reviews MUST create review tasks with the four outcomes in US5 and track overdue reviews.
- **FR-013 / POL-013**: Comprehension checks MUST support question banks, pass marks, attempts and accessible delivery.
- **FR-014 / POL-014**: Exemptions MUST support request, route, scope, reason, expiry and reminders.
- **FR-015 / POL-015**: Policies MUST be mappable to controls and regulation clauses from the control library of spec 026.
- **FR-016 / POL-016**: Evidence packs MUST be generated in the background in PDF and CSV, audited, and shareable to guest auditors with expiry.
- **FR-017 / POL-017**: New joiners MUST get attestation tasks for policies flagged "onboarding required" through the onboarding journey of spec 022.
- **FR-018 / POL-018**: Policy pages MUST be indexed for search with clause anchors, and superseded versions MUST be excluded from default search results.
- **FR-019**: The policy register MUST follow DLS-079: below 640px each policy is a stacked card with its name, status, owner and next review date, and no text breaks inside a word. Acceptance: at 390px the register shows every policy as a readable card with no horizontal page scroll.
- **FR-020**: My policies MUST open with a status summary (spec 036 DLS-103, DLS-109): "You are up to date" with the date of the last acknowledgement, or the policies waiting for the reader, each with its key changes, its version and one action to read and acknowledge; then recently updated policies with what changed. An acknowledgement gives a receipt naming the policy and version.
- **FR-021**: The policy register MUST open with what needs attention (reviews overdue or due within 30 days) before the full list, hide the status column while every listed policy shares one status and say it once above the table, support keyboard sorting, and use the filter toolbar (spec 036 DLS-106). The policy reader uses the reading column with a sticky "On this page" list and a scoped "Ask about this policy" action (spec 014 FR-027).

## 6. Business and validation rules
- One current version per policy at a time. An approved version with a future effective date is "approved, not yet effective".
- Material or minor is chosen by the owner at publish and confirmed by the last approver.
- Default deadlines: 30 days for campaigns, 14 days for late joiners. Tenant configurable.
- Attestation records are kept for the life of the tenant by default, and never less than 7 years after the person leaves. Legal hold applies (spec 011).
- Comprehension answers are visible only to compliance officers, not managers.

## 7. Tenant and organization scope
- Tenant-owned data: register, versions, campaigns, attestations, checks, exemptions, mappings, evidence packs.
- Organization scoping: policies can be tenant-wide or scoped to an org subtree (for example a subsidiary policy). Compliance officers can be scoped to a subtree.
- Cross-tenant denial cases: attestation lookup by ID; evidence pack download; assistant retrieval of another tenant's policy.
- Sibling, parent and child boundary cases: a subsidiary policy for "Acme Gulf" is not shown to "Acme Egypt"; a group policy applies to children only if its audience includes the subtree; a scoped compliance officer cannot see sibling completion.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Policy and versions | Product | Per policy, default Internal | Life of tenant | Owner and approver IDs |
| Attestation record | Product | Confidential | Life of tenant, min 7 years after leaving | Yes |
| Comprehension result | Product | Confidential | Same as attestation | Yes |
| Exemption | Product | Confidential | 7 years after expiry | Yes |
| Evidence pack | Product (object storage) | Confidential | 90 days, then deleted | Yes |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `policy.core` (register, approval, publish, one attestation campaign at a time) | on | on | on |
| `policy.campaigns` (unlimited campaigns, cycles, escalation) | off | on | on |
| `policy.comprehension` | off | on | on |
| `policy.exemptions` | off | on | on |
| `policy.evidence` (control mapping, evidence packs) | off | off | on |
| `ai.policy_qa` (counts against AI quota) | on | on | on |

## 10. UX, localization and accessibility
- My policies page: action needed, accepted, all policies, with search and categories.
- Policy page: clause navigation, version and effective date, owner, "Ask about this policy", attest button when required, history for permitted users.
- Arabic and English side by side view for bilingual policies.
- Attestation confirm is a clear, labelled action; the attestation text is read aloud by screen readers before the button.

## 11. AI behavior and data policy
- Policy Q&A uses the retrieval of spec 014 restricted to policy sources, with the user's permissions and the policy's classification.
- Answers quote clauses verbatim where possible and always cite; no-evidence answers say so (constitution VII).
- AI may suggest a plain-language summary of a policy for the owner to approve; the approved summary is shown above the clauses and labelled.
- AI comparison between versions (R2) produces a change summary for approvers; it does not replace the compare view.

## 12. Integrations and events
- Publishes: `PolicyPublished`, `PolicySuperseded`, `PolicyAttested`, `AttestationOverdue`, `PolicyReviewDue`, `ExemptionApproved`, `ExemptionExpired`.
- Consumes: `UserProvisioned`, `OrganizationMembershipChanged`, `UserDeprovisioned`, onboarding events from spec 022, approval results from spec 024.
- Optional push of attestation completion to HR (spec 019) or a GRC tool through the connector framework (spec 017).

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| policy.created / submitted / approved / returned / published / superseded / retired | audit | policy, version, actor |
| policy.campaign_launched / closed | audit | policy, version, audience size, deadline |
| policy.attested | audit | policy, version, user, channel |
| policy.exemption_requested / approved / expired | audit | policy, person or unit, approver |
| policy.evidence_exported / shared | audit | policies, range, actor, guest space |
| policy.review_completed | audit | policy, outcome, reviewer |
| policy.viewed, policy.qa_asked, policy.qa_no_answer | analytics | policy, unit (aggregated) |

## 14. Security and abuse cases
- Forging attestations: records are written only by the attesting user's own session, never by API on behalf of another user, never by delegates or admins. The only exception is the paper import (R2): offline attestations are imported with source "paper", the importer recorded and the scanned evidence attached, and they are reported apart from online attestations. Bulk "mark as attested" does not exist.
- Reading comprehension answers to embarrass staff: restricted to compliance officers, access audited.
- The assistant leaking restricted policy names: retrieval is permission filtered before generation.

## 15. Non-functional requirements
- Compliance view for a policy with 100,000 people loads p95 under 2 seconds.
- Campaign launch to 100,000 people creates all tasks within 5 minutes.
- Evidence pack for 20 policies and 50,000 people is generated within 15 minutes.

## 16. Success criteria
- **SC-001**: A compliance officer launches a yearly attestation campaign in under 15 minutes.
- **SC-002**: An auditor request for "who accepted version 3 of the Code of Conduct and when" is answered by export in under 5 minutes.
- **SC-003**: In acceptance tests, the assistant cites a clause for 95% of answerable policy questions and refuses 100% of unanswerable ones without inventing policy.

## 17. Open questions
- [NEEDS CLARIFICATION: is policy management sold in Workplace or only in higher packages?] Default: core in all packages, as written in section 9.
- [NEEDS CLARIFICATION: do customers need paper attestation import in R1?] Default: R2.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| EXP-004 | FR-002, FR-004, US1 |
| KNO-001 | FR-003 |
| KNO-007 | FR-010, FR-011, US4 |
| UC-04 | FR-006, US2 |
| UC-06 | US4 |

## Review checklist
- [x] No implementation details
- [x] Every FR is testable and traces to a requirement ID or is marked new
- [x] Cross-tenant and org boundary cases are listed
- [x] Entitlement behavior is defined
- [x] Audit events are defined
- [ ] No open [NEEDS CLARIFICATION] items that change scope
