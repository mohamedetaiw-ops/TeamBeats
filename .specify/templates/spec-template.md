# Feature Specification: [FEATURE NAME]

| Field | Value |
|---|---|
| Spec ID | [NNN-short-name] |
| Area | [platform / experience / knowledge / workflows / ai / integrations / analytics] |
| Status | Proposed |
| Version | 0.1 |
| Owner | [name] |
| Approver | [name] |
| Target release | [R0 / R1 / R2 / R3] |
| Last updated | [YYYY-MM-DD] |
| Requirement IDs | [e.g. TEN-001, TEN-002] |
| Use cases | [e.g. UC-01] |
| Depends on | [other spec IDs] |

<!--
Rules for writing this spec:
- Describe WHAT and WHY. Leave HOW (tables, endpoints, libraries) to plan.md.
- Do not invent product rules. Mark gaps as [NEEDS CLARIFICATION: question] and give the safe default you would use.
- Every acceptance scenario must be testable. Include positive, negative, boundary, permission and failure cases.
- Delete sections that truly do not apply, and say why in one line.
-->

## 1. Problem and outcome

**Problem:** [who has what problem today]

**Outcome:** [what is true when this ships]

## 2. Scope

**In scope:**
- ...

**Out of scope:**
- ...

## 3. Actors and permissions

| Actor | What they can do in this feature | Permission / scope required |
|---|---|---|

## 4. User scenarios

### User Story 1: [title] (Priority: P1)

[Plain-language journey]

**Why this priority:** ...

**Independent test:** [how this story can be demonstrated on its own]

**Acceptance scenarios:**
1. **Given** ..., **When** ..., **Then** ...

### Edge cases and exception flows
- ...

## 5. Functional requirements

- **FR-001**: The system MUST ... *(traces to: XXX-000)*

## 6. Business and validation rules
- ...

## 7. Tenant and organization scope

- Tenant-owned data: [yes/no, which]
- Organization scoping: [none / single unit / configurable subtree]
- Cross-tenant denial cases: [list]
- Sibling / parent / child boundary cases: [list]

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|

## 9. Package entitlements

| Capability key | Package behavior | Quota |
|---|---|---|

## 10. UX, localization and accessibility
- Design system (constitution XXXI, spec 036): templates and components used from `docs/design-system`; any new component, token or icon needed
- States: empty, loading, error, degraded (AI or connector unavailable), no permission
- Locales and RTL notes
- WCAG 2.2 AA notes

## 11. AI behavior and data policy
[Only if the feature uses AI. Classification limits, provider policy, citations, no-evidence behavior, untrusted content handling.]

## 12. Integrations and events
- Events published / consumed
- External systems touched (through connector adapters only)

## 13. Audit, telemetry and analytics

| Event | Type (audit / telemetry / analytics) | Key fields |
|---|---|---|

## 14. Security and abuse cases
- ...

## 15. Non-functional requirements
- ...

## 16. Success criteria
- **SC-001**: [measurable, technology-free outcome]

## 17. Open questions
- [NEEDS CLARIFICATION: ...] Default if unanswered: ...

## 18. Traceability

| Requirement ID | Covered by |
|---|---|

## Review checklist
- [ ] No implementation details (languages, frameworks, table names)
- [ ] Every FR is testable and traces to a requirement ID or is marked new
- [ ] Cross-tenant and org boundary cases are listed
- [ ] Entitlement behavior is defined
- [ ] Audit events are defined
- [ ] No open [NEEDS CLARIFICATION] items that change scope
