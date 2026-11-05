# Specs index

Status values: Proposed, Approved, In Progress, Implemented, Deprecated, Retired. Only Approved specs may be implemented.

| Spec | Area | Release | Status | Requirement IDs |
|---|---|---|---|---|
| [001 Tenant management](001-tenant-management/spec.md) | platform | R0 | Approved | TEN-001, TEN-002, TEN-009, TEN-010, ADM-001 |
| [002 Organization hierarchy](002-organization-hierarchy/spec.md) | platform | R0, R1 to R2 (AD mapping) | Approved; AD mapping pending approval | TEN-003 to TEN-006, TEN-010, INT-007 (directory) |
| [003 Identity and authentication](003-identity-and-authentication/spec.md) | platform | R0 to R2 | Approved; AD sign-in and sync pending approval | IAM-001 to IAM-007, IAM-009, IAM-010, INT-007 (directory) |
| [004 Authorization and permissions](004-authorization-and-permissions/spec.md) | platform | R0 | Approved | TEN-007, TEN-008, IAM-007, IAM-008, ADM-001 |
| [005 Packages and entitlements](005-packages-and-entitlements/spec.md) | platform | R0, R2 | Approved | AI-001, ADM-001, ENT-001 to ENT-008 (new) |
| [006 Audit and admin shell](006-audit-and-administration/spec.md) | platform | R0 | Approved | ADM-001 to ADM-003, TEN-010, AUD-001 to AUD-007 (new) |
| [007 Localization and accessibility](007-localization-and-accessibility/spec.md) | experience | R0 to R2 | Approved | LOC-001 to LOC-004, EXP-008 |
| [016 Self services, service catalogue and workflows](016-service-catalogue-and-workflows/spec.md) | workflows | R1, R2 | Proposed | WF-001 to WF-016, UC-08 |

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
| 008 Employee home and personalization | R1 | EXP-001, EXP-003. Widgets the user can add, remove and reorder; task badge; My services strip; leave calendar widget; tool box (weather, world clock) |
| 009 Content, news and publishing | R1 | EXP-002 to EXP-004, UC-04. Announcements with date filter, news, policies and handbooks as searchable pages (not PDFs), corporate offers, internal vacancies, promotion lists |
| 010 Employee directory and org chart | R1 | EXP-005, EXP-006, UC-05. Profile fields and reporting line from AD (spec 003), My team, newcomers |
| 011 Document library | R1 | KNO-001 |
| 012 Search and retrieval | R1 | KNO-002 to KNO-006. Type-ahead; indexes people, pages, documents and services |
| 013 AI Gateway | R1 | AI-002 to AI-006, AI-011, AI-012 |
| 014 AI assistant and RAG | R1 | AI-005, AI-007, KNO-007, UC-06 |
| 015 Notifications and email | R1 | INT-008 (new NOT- IDs). In-app notification centre replaces the old "Intra mail" inbox |
| 017 Connector framework | R2 | INT-001, INT-009, INT-010 |
| 018 Microsoft 365 and SharePoint | R2 | INT-002, INT-003, UC-10 |
| 019 HR integration | R2 | INT-007, UC-09. Payslip view (read-only, Confidential), HR record prefill; AD covers the directory part (specs 002, 003) |
| 020 Agents and tools | R3 | AI-008 to AI-010, UC-07 |
| 021 Analytics | R2 | LOC-005, LOC-006 (rename to ANL-001, ANL-002). Request cycle times, exit reasons, attendance reports |
| 022 Leave and attendance | R2 | New LVA- IDs. Leave request with live balance, leave types, replacement person, team leave calendar and overlap view, attendance sign in and out, working hours and exceptions, compensation days. Registers its services and tasks in 016. Can read balances from HR (019) instead of keeping them |
| 023 Communities and engagement | R2 | EXP-007. Discussion channels (public and per department), Speak Up with anonymous option, polls, recognition (team and star of the period), celebrations, photo gallery, points and leaderboard |

## Requirement coverage
Every foundation requirement ID is mapped above to a written or planned spec, except these, which belong to later R2 and R3 specs: EXP-009 (PWA), INT-004 to INT-006 (Google, Oracle, SAP packs), AI-013 (usage visibility, likely in 013).

## Current intranet coverage

Source: functional review of the current Link Development intranet (Performly "Intra"), 2026-11-05. Every function maps to a spec. "Linked" means the catalogue (016) opens the other system.

| Current function | TeamBeats home | Release |
|---|---|---|
| Hamburger menu, quick links ribbon, home widgets as three routes to services | One Services catalogue (016), personal My services strip on home (008) | R1 |
| Header: clock, search, task badge, profile menu | Header in 008, search in 012, task inbox in 016 | R1 |
| Attendance sign in and out, working hours, productivity, punctuality, compensation day | 022 | R2 |
| Leave request with live balance, my and team leave, overlap detector, leave calendar | 022, calendar widget in 008 | R2 |
| Pending tasks, subordinates' tasks | Task inbox (016) | R1 |
| Manage replacement (delegate while away) | Delegation (016) | R1 |
| Payslip | 019 (read from payroll or HR) | R2 |
| Training and Udemy requests, HR appointments, resignation | Form services (016); Udemy as linked app | R1 form, R2 templates |
| Expenses, purchase request, business cards, transportation, maintenance, travel, parking, referrals | Form services from the starter templates (016); ERP hand-off via 017 | R2 |
| Meeting room booking | Microsoft 365 room mailboxes through 018 | R2 |
| Help Desk, VPN request, Es2alny FAQ | Help Desk as linked app or form service (016); VPN as form service; FAQ answered by the AI assistant (014) | R1 |
| Announcements, news, condolences, policy notices | 009 | R1 |
| Handbooks, policies, career paths and job descriptions, learning library | Searchable pages and documents (009, 011, 012) | R1 |
| Corporate offers, internal vacancies, promotion lists | 009 | R1 |
| Discussion channels, Speak Up, poll, gallery, team and star of the period, celebrations, banking points | 023 | R2 |
| Newcomers, My profile, team and peers | 010 | R1 |
| Profile photo, theme, language | 010 (photo), 008 (theme), 007 (language) | R1 |
| Change password | Not in TeamBeats: handled by AD or Entra ID self-service password reset | n/a |
| Email signature | Not carried over (belongs to the mail system) | n/a |
| Intra mail inbox | Notification centre (015) | R1 |
| Tool box: weather, world clock, time zone converter | Home widget (008) | R1 |
| Linkawy360, Talent, Agile, Tech Hub, PAL, SPIN, Happiness Circle | Linked apps (016) with SSO cue | R1 |
| iOS and Android apps | Responsive web and PWA (EXP-009); native apps are an open foundation decision | R2 |
| Five languages (en, ar, hi, bn, fr) | ar and en in R1; fr, ur, hi in R2. Bengali is not planned | R1, R2 |

Gaps in the current intranet that the specs close: single catalogue page (016), one look for all services (007, 016), policies as searchable pages (009, 012), search that finds services and documents (012), clear cue and SSO for other systems (016), personalizable home (008), one discussion source for widget and list (023), labelled icons and AA contrast (007).
