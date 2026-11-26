# Implementation Plan: Policy Management

**Spec:** `specs/025-policy-management/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0013, spec 009 plan (body document, acknowledgement pattern), spec 011 plan (controlled documents), spec 024 plan (sequential approval steps), module map (`Policies`)

## Summary
R1 builds the policy register, authoring as structured pages with numbered clauses or as a controlled document, approval with clause comments, publishing with effective dates and a legally binding language (User Story 1), attestation campaigns with reminders, late joiners and leavers (User Story 2), the compliance view (User Story 3), and policy Q&A through the assistant (User Story 4). Scheduled review cycles, comprehension checks, exemptions, control mapping and evidence packs are R2.

R1 slices, in order:
1. **Register and lifecycle:** policies, versions with clauses, lifecycle and audit.
2. **Approval:** sequential route on the engine core, compare against the version in force, clause comments, all locale variants approved together.
3. **Attestation:** campaigns, inbox tasks, immutable records, reminders and escalation, audience changes.
4. **Compliance view:** completion by unit and person, per-person view, scoped to the viewer.
5. **Q&A:** policy clauses indexed with anchors for search and the assistant (planned specs 012 and 014); the policy-scoped entry on each page.
6. **Pages:** register, policy editor, policy page with clause anchors, My policies, campaign and compliance pages, in English and Arabic, RTL.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation and org scope | Yes | All tables tenant-owned with forced RLS; an org-scoped compliance officer sees people of their subtree only |
| III authorization | Yes | `policy.author`, `policy.manage` through `IAuthorizationService`; owners cannot approve their own version |
| VII, X AI | Yes | Q&A goes through the assistant and the AI Gateway with permission-filtered retrieval of clauses; it never answers as policy from general knowledge |
| XIV modules | Yes | `Policies` owns policies and attestations; routes on `Workflow`; documents through the `Documents` contract |
| XXI entitlements | Yes | `policy.core` allows one open campaign at a time; `policy.campaigns` lifts it; `ai.policy_qa` for Q&A |

## Data model (schema `policies`)
- `policy`: `code`, names per locale, `category`, `owner_user_id`, `audience jsonb` (same model as spec 009), `classification`, `binding_locale`, `status`, `current_version`, `effective_from`, `next_review_at`, `related jsonb`.
- `policy_version`: `number`, `format` (page or document), `variants jsonb` per locale (clauses: number, anchor, title, body document), `document_ref` (spec 011 version), `material bool`, `approved_by jsonb`, `effective_from`, `state`.
- `clause_comment`: version, clause anchor, author, text, resolved.
- `campaign`: `policy_id`, `version`, `audience`, `deadline`, `attestation_text` per locale, `reminder_days int[]` (default 7 and 2), `late_joiner_days` (14), `excluded_membership_types`, `status`.
- `campaign_member`: user, joined_at, due_at, status (open, attested, overdue, left_audience), left_at.
- `policy.onboarding_required` (FR-017, R2 with spec 022 T019) and `obligation`: an attestation one person owes outside any campaign, asked for by an onboarding journey step (the source key), due with the step, open, attested or cancelled.
- `attestation`: insert only: user, policy, version, campaign (none for an obligation), text hash, attestation text, channel, time (FR-007). Attestation cannot be delegated, so the delegation contract of spec 016 is not consulted for these tasks.

## Attestation flow
- Launch materializes `campaign_member` rows from the audience and creates one inbox task per person through the spec 016 task source contract (not an engine instance per person, to keep 100,000-person campaigns cheap).
- `OrganizationMembershipChanged` adds late joiners with their own due date and marks leavers `left_audience`.
- A scheduler sends reminders and, after the deadline, marks members overdue and sends manager lists. Manager lists wait for the reporting line (planned spec 010).
- Onboarding (FR-017): the spec 022 journey asks through `IPolicyObligations` for every published policy flagged "onboarding required" that applies to the new joiner and whose version in force they have not attested to. Obligations show in the inbox and on My policies like campaign attestations; one attestation to the version in force settles them, and `PolicyAttested` tells the journey. Overdue onboarding attestations follow the journey's own overdue escalation, not the campaign scheduler.

## Compliance view (US3)
Counts by org unit (with subtree roll-up) and drill-down to people; "location" and "manager" breakdowns arrive with spec 010, recorded in the notes. Line managers see their reports' status once spec 010 exists.

## Q&A (US4)
`Policies` publishes `PolicyPublished` with clause anchors; the search index (planned spec 012) stores each clause as a unit with policy, version, audience and permissions, excluding superseded versions by default (FR-018). The assistant (planned spec 014) retrieves clauses with the user's permissions and cites title, version and clause anchor. The policy-scoped entry passes a filter on the policy ID. Built when specs 012 and 014 exist; the plan keeps clause anchors stable across versions so citations survive.

## API (v1)
- Register: `GET/POST /api/v1/policies`, `GET /api/v1/policies/{id}`, `PUT .../draft`, `POST .../submit`, `GET .../versions/{n}`, `GET .../compare`, clause comments.
- Campaigns: `POST /api/v1/policies/{id}/campaigns`, `GET .../campaigns/{cid}`, `POST .../remind`, `POST .../close`.
- Employee: `GET /api/v1/me/policies`, `POST /api/v1/policies/{id}/attest`.
- Compliance: `GET /api/v1/policies/{id}/compliance?unit=`, `GET /api/v1/policies/compliance/people/{userId}`.

## Tests
- Integration per user story: US1 scenarios 1 to 5, US2 scenarios 1 to 7, US3 scenarios 1 to 3 (4 with spec 010), US4 scenarios with the assistant once it exists.
- Boundaries: policies, campaigns and attestations of another tenant not found; org-scoped compliance officer limited to their subtree; a user outside the audience gets no task.
- Entitlements: a second open campaign refused without `policy.campaigns`.
- Immutability: attestation rows cannot be updated or deleted by the app role.
- Acceptance (e2e): publish a policy through two approvers, launch a campaign, attest, read completion, in English and Arabic with axe.

## R3 plan (2026-11-26)

**Slice 1, change impact (US9, FR-022, FR-023):** `GET /api/v1/policies/{id}/impact` returns the facts for the working version against the version in force, for the same people as the compare view (editors, managers and route reviewers): changed clauses (from `ClauseRules.Compare`), attestations of the version in force, approved exemptions, comprehension questions of the version in force whose clause changed, linked controls, related policies the reader may see, and the onboarding flag. `POST /api/v1/policies/{id}/impact/assessment` asks the AI Gateway (use case summarize, the policy's classification, JSON schema `impacts` of clause number, control codes, policy ids and a reason) with the changed clauses, the controls with titles and the related policies with names as data. Answers naming anything outside those lists are dropped. The assessment is kept in `policies.impact_assessment` per working version and locale with the hash of what it read, like the change summary. Audit `policy.impact_assessed`. The compare page shows the impact beside the change summary, the assessment labelled as AI-generated.

**Slice 2, regulator change feeds (US10, FR-024, FR-025):** capability `policy.regulator_feeds` (Enterprise and Government). Integrations pack `regulator_feed` (spec 017 REST pack with the tenant allowlist and sign-in) with one read `list_changes`: `GET {address}` answering a JSON Feed (`items` with `id`, `title`, `url`, `date_published`, `summary` or `content_text`) or an object or array of items with `id`, `title`, `link`, `published` and `summary`; at most 200 items, only those five fields leave the pack, the summary cut to 2,000 characters and the link kept only when https. Contract `IRegulatorFeeds` in Integrations.Contracts. The Policies maintenance job reads each enabled feed connection at most every 6 hours, stores new items in `policies.regulator_change` (unique per connection and item id), tells the tenant's compliance officers once per read that found new items (`policies.regulator_changes_new`), and deletes items older than 2 years. `GET /api/v1/policies/regulator-changes?status=` lists them for `policy.manage`; `POST /api/v1/policies/regulator-changes/{id}:dismiss` and `:link` with policy ids and whether to start reviews. A started review is a `policy_review` due in 30 days with the change recorded on it, told to the owner like a scheduled review; a policy with an open review keeps it. Audit `policy.regulator_change_decided`. Page `/policies/regulator-changes` from the register.

Decisions: feeds are JSON only for now (JSON Feed or a plain list), since the connector runtime reads JSON; RSS and Atom wait for an XML reader in spec 017. A started review is due in 30 days. Notices go to compliance officers at tenant scope.

Tests: impact facts against a known draft, AI assessment keeps only listed controls and policies, policy blocked classification, people who may not compare get not found, other tenant; feed reading every 6 hours, only the five fields kept, non-https links dropped, notices once per read, dismiss and link with reviews, a policy with an open review keeps it, entitlement off, other tenant. e2e for the impact on the compare page and for Regulator changes, in English and Arabic with axe.
