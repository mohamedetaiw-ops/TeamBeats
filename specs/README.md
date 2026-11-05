# Specs index

Status values: Proposed, Approved, In Progress, Implemented, Deprecated, Retired. Only Approved specs may be implemented.

| Spec | Area | Release | Status | Requirement IDs |
|---|---|---|---|---|
| [001 Tenant management](001-tenant-management/spec.md) | platform | R0 | Approved | TEN-001, TEN-002, TEN-009, TEN-010, ADM-001 |
| [002 Organization hierarchy](002-organization-hierarchy/spec.md) | platform | R0 | Approved | TEN-003 to TEN-006, TEN-010 |
| [003 Identity and authentication](003-identity-and-authentication/spec.md) | platform | R0 to R2 | Approved | IAM-001 to IAM-007, IAM-009, IAM-010 |
| [004 Authorization and permissions](004-authorization-and-permissions/spec.md) | platform | R0 | Approved | TEN-007, TEN-008, IAM-007, IAM-008, ADM-001 |
| [005 Packages and entitlements](005-packages-and-entitlements/spec.md) | platform | R0, R2 | Approved | AI-001, ADM-001, ENT-001 to ENT-008 (new) |
| [006 Audit and admin shell](006-audit-and-administration/spec.md) | platform | R0 | Approved | ADM-001 to ADM-003, TEN-010, AUD-001 to AUD-007 (new) |
| [007 Localization and accessibility](007-localization-and-accessibility/spec.md) | experience | R0 to R2 | Approved | LOC-001 to LOC-004, EXP-008 |

Open questions across these specs, with defaults: [open-questions.md](open-questions.md). Release plan: [roadmap](../docs/product/roadmap.md).

## R0 build order

Each spec folder now has `spec.md` (Approved), `plan.md` and `tasks.md`. Shared setup is in `docs/architecture/r0-solution-structure.md` and in Phase 1 of `001-tenant-management/tasks.md`.

1. 001 Phase 1: platform setup, plus ADR-0006 to ADR-0008
2. 006 Phase 1 to 2 (`IAuditWriter`) and 007 Phase 1 to 3 (localization gates), in parallel
3. 001 and 005 core (tenants need packages)
4. 002, then 004 (authorization needs the hierarchy)
5. 003 R0 parts (dev sign-in, sessions)
6. 006 admin shell and the rest of 007
7. R0 exit: all cross-tenant and org boundary suites green

## Next specs (not yet written)

| Planned spec | Release | Requirement IDs |
|---|---|---|
| 008 Employee home and personalization | R1 | EXP-001, EXP-003 |
| 009 Content, news and publishing | R1 | EXP-002 to EXP-004, UC-04 |
| 010 Employee directory and org chart | R1 | EXP-005, EXP-006, UC-05 |
| 011 Document library | R1 | KNO-001 |
| 012 Search and retrieval | R1 | KNO-002 to KNO-006 |
| 013 AI Gateway | R1 | AI-002 to AI-006, AI-011, AI-012 |
| 014 AI assistant and RAG | R1 | AI-005, AI-007, KNO-007, UC-06 |
| 015 Notifications and email | R1 | INT-008 (new NOT- IDs) |
| 016 Forms, service catalogue and workflows | R2 | WF-001 to WF-003, UC-08 |
| 017 Connector framework | R2 | INT-001, INT-009, INT-010 |
| 018 Microsoft 365 and SharePoint | R2 | INT-002, INT-003, UC-10 |
| 019 HR integration | R2 | INT-007, UC-09 |
| 020 Agents and tools | R3 | AI-008 to AI-010, UC-07 |
| 021 Analytics | R2 | LOC-005, LOC-006 (rename to ANL-001, ANL-002) |

## Requirement coverage
Every foundation requirement ID is mapped above to a written or planned spec, except these, which belong to later R2 and R3 specs: EXP-007 and EXP-009 (communities, PWA), INT-004 to INT-006 (Google, Oracle, SAP packs), AI-013 (usage visibility, likely in 013).
