# Feature Specification: Project and Team Spaces

| Field | Value |
|---|---|
| Spec ID | 029-project-and-team-spaces |
| Area | workflows |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R2 (spaces for projects and teams, members and guests, task lists and board view, assignees, due dates, milestones and progress, space files from 011, space discussion from 023, team calendar, space templates, my tasks across spaces), R3 (dependencies and timeline view, time tracking, Planner and Jira sync, AI status summaries) |
| Last updated | 2026-11-07 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | PRJ-001 to PRJ-018 (new) |
| Use cases | UC-03 |
| Depends on | 002, 004 (space roles), 005, 010 (people), 011 (space document library), 012 (search), 015 (notifications), 016 (task inbox shows space tasks), 018 (Outlook calendar overlay), 022 (calendars), 023 (discussions), 026 (guest access) |
| Used by | 008 (My tasks and space widgets), 021 (counts) |

## 1. Problem and outcome

**Problem:** Teams run cross-department projects in chat groups and spreadsheets. Files are scattered, nobody knows the status, and partners outside the company get emailed attachments.

**Outcome:** A space gives a project or team one place for tasks, milestones, files, discussion and a calendar, with clear members and optional guests. It is built from existing parts: documents from 011, discussions from 023, guest identity from 026, notifications from 015. My tasks shows a person's work across spaces next to their workflow tasks (016).

## 2. Scope

**In scope:**
- Spaces of type project (has start, end, milestones, status) or team (ongoing); public to the tenant, private to members, or secret.
- Members with roles owner, member and viewer; guests invited by email with the guest rules of 026.
- Tasks: title, description, assignees, due date, priority, labels, checklist items, attachments, comments; list and board views with custom columns.
- Milestones with target dates and progress from linked tasks; project status (on track, at risk, off track) set by owners with a note.
- Space library: a document library from 011 with the space's members as its audience.
- Space discussion: a 023 channel bound to the space.
- Team calendar: space events plus members' leave (022) and, when the user allows, Outlook free and busy (018).
- Templates: create a space from a template with lists, columns and starter tasks.
- My tasks: tasks assigned to me across spaces, sorted by due date, also shown in the 016 inbox as a separate tab.
- Archive and restore spaces; export a space's task list as CSV.

**Out of scope:**
- Gantt charts, dependencies and resource levelling (R3 timeline only).
- Time sheets and billing.
- Two-way sync with Planner, Jira or Asana (R3).

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Space creator | Create spaces of allowed types | `spaces.create` in org scope |
| Space owner | Manage settings, members, guests, templates, status, archive | Owner relation |
| Space member | Create and edit tasks, comment, upload files, post | Member relation |
| Space viewer | Read tasks, files and discussion | Viewer relation |
| Guest | Act as member or viewer in invited spaces only | Guest identity (026), relation on the space |
| Spaces administrator | Set tenant rules (who can create, guest policy, templates), see all spaces' metadata | `spaces.manage` |

## 4. User scenarios

### User Story 1: Start a project space (Priority: P1, R2)

Omar creates "New ERP rollout" from the project template with 12 members.

**Why this priority:** the container for every other story.

**Independent test:** create a private project space, add members, check that non-members cannot see it or find its content.

**Acceptance scenarios:**
1. **Given** the project template, **When** Omar creates a space, **Then** it gets the template's lists, columns and starter tasks, a library and a discussion channel, and Omar is owner.
2. **Given** a private space, **Then** non-members cannot open it, see its tasks in search, or receive its notifications; a secret space does not appear in the space directory.
3. **Given** a public space, **Then** anyone in the tenant can read it and join as member if the owner allows.

### User Story 2: Track work (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** a task assigned to Laila with a due date, **Then** she is notified, it appears in My tasks and in the 016 inbox tab, and she gets a reminder the day before.
2. **Given** the board view, **When** a member drags a task to "Done", **Then** it is completed, milestone progress updates, and watchers are notified in a grouped notification.
3. **Given** a viewer, **Then** they cannot change tasks; the server refuses edits regardless of the UI.
4. **Given** a task with a checklist, **Then** progress shows the checked share.

### User Story 3: See project status (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** milestones with linked tasks, **Then** the overview shows progress per milestone, overdue tasks and the owner's status with its note and date.
2. **Given** a status change to "at risk", **Then** members and followers are notified, and the change is kept in the status history.

### User Story 4: Work with a partner (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** guests are allowed by tenant policy (026), **When** the owner invites a partner by email, **Then** the partner signs in as a guest, sees only that space, and is labelled "Guest" everywhere.
2. **Given** a guest, **Then** they cannot search the tenant, open the directory beyond space members, or see other spaces.
3. **Given** a library file classified Confidential, **Then** guests cannot open it unless the space owner and the classification policy allow it.
4. **Given** guest access expires (default 90 days), **Then** access ends and the owner is told 7 days before.

### User Story 5: Share files and talk in one place (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** the space library, **Then** files follow 011 rules (versions, preview, scanning) with the space's members as audience.
2. **Given** the space discussion, **Then** it behaves as a 023 channel whose members are the space members.
3. **Given** a task, **When** a member links a library file, **Then** the task shows the file and its current version.

### User Story 6: Plan around people (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** the team calendar, **Then** it shows space events, milestones, task due dates and members' approved leave (022) without leave types.
2. **Given** a member allowed free and busy sharing (018), **Then** their busy times show without subjects.

### User Story 7: See all my tasks (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** tasks in several spaces, **When** Laila opens My tasks, **Then** she sees them by due date with the space name, and can complete them there.
2. **Given** she leaves a space, **Then** its tasks leave My tasks and stay assigned in the space until reassigned by an owner.

### Edge cases and exception flows
- Last owner leaves the company: ownership passes to the space creator's manager, or the spaces administrator is asked.
- Archived space: read-only, hidden from My tasks, files kept per 011 retention.
- Task assigned to a guest whose access expired: shown as unassigned with a notice.
- Template changed: existing spaces are not affected.

## 5. Functional requirements

- **PRJ-001 / FR-001**: Spaces MUST support project and team types with public, private and secret visibility.
- **PRJ-002 / FR-002**: Space roles MUST be relations in 004 checked through `IAuthorizationService`.
- **PRJ-003 / FR-003**: Private and secret space content MUST be invisible to non-members in every surface, including search and notifications.
- **PRJ-004 / FR-004**: Tasks MUST support assignees, due dates, priority, labels, checklists, attachments and comments, in list and board views.
- **PRJ-005 / FR-005**: Milestones MUST show progress from linked tasks; status MUST have a history with notes.
- **PRJ-006 / FR-006**: Each space MUST have a 011 library and a 023 channel with the space's members as audience.
- **PRJ-007 / FR-007**: Guests MUST follow 026 guest policy, see only invited spaces, and expire by default after 90 days.
- **PRJ-008 / FR-008**: Classified files MUST be blocked for guests unless both the owner and the classification policy allow.
- **PRJ-009 / FR-009**: The team calendar MUST show space events, milestones, due dates and approved leave without leave type.
- **PRJ-010 / FR-010**: My tasks MUST list tasks across spaces and appear as a tab in the 016 inbox.
- **PRJ-011 / FR-011**: Assignment, due reminders and status changes MUST notify through 015 with grouping.
- **PRJ-012 / FR-012**: Templates MUST create lists, columns and starter tasks; template changes MUST NOT alter existing spaces.
- **PRJ-013 / FR-013**: Archived spaces MUST be read-only and restorable by owners or the spaces administrator.
- **PRJ-014 / FR-014**: Ownerless spaces MUST be reassigned per section 4 edge cases.
- **PRJ-015 / FR-015**: Task lists MUST export as CSV for members.
- **PRJ-016 / FR-016**: Space tasks and metadata MUST be indexed in 012 with space access rules.
- **PRJ-017 / FR-017**: The tenant MUST be able to restrict who creates spaces and whether guests are allowed.
- **PRJ-018 / FR-018**: All user-facing text MUST use resource keys and work in RTL, including the board view.

## 6. Business and validation rules
- Up to 5,000 tasks per space, 500 members, 50 guests.
- Guest expiry default 90 days, range 7 to 365.
- Due reminder the day before at 09:00 in the assignee's time zone.

## 7. Tenant and organization scope
- Tenant-owned data: spaces, members, tasks, milestones, statuses, templates.
- Organization scoping: space creation can be limited to org units; public spaces are tenant-wide.
- Cross-tenant denial cases: a guest of tenant A invited to a space has no access to tenant B even with the same email; task search never crosses tenants.
- Sibling, parent and child cases: a space created by Acme Gulf may include Acme Africa members when invited; org scope does not grant space access.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Spaces, tasks, milestones | Product | Internal by default, owner may set Confidential | Life of space plus tenant policy | Yes (assignees) |
| Guest memberships | Product | Internal | Until expiry plus 90 days | Yes |
| Status history | Product | Internal | Life of space | Yes |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `spaces.projects` | off | on | on |
| `spaces.guests` | off | on | on |
| `spaces.max_spaces` (limit) | 0 | 200 | unlimited |

## 10. UX, localization and accessibility
- Space directory, space overview, tasks (list and board), library, discussion, calendar, settings.
- Board drag and drop has a keyboard alternative (move to column menu); columns flow right to left in RTL.
- Arabic and English.

## 11. AI behavior
R2: none. R3: status summaries through 013 using only tasks the reader can see.

## 12. Integrations and events
- Publishes `SpaceCreated`, `SpaceArchived`, `TaskAssigned`, `TaskCompleted`, `MilestoneReached`, `SpaceStatusChanged`.
- Consumes leave events (022), guest expiry (026), people changes (010).

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| spaces.created / archived / restored | audit | space, type, visibility, actor |
| spaces.member_added / removed | audit | space, member, role, actor |
| spaces.guest_invited / expired | audit | space, guest, actor |
| spaces.task_activity | telemetry | space, action (no content) |

## 14. Security and abuse cases
- Guest overreach: guests see only invited spaces; directory limited to space members.
- Secret space leakage: excluded from directory, search and notifications to non-members.
- Data exfiltration by guests: classification gate on files; downloads audited by 011.

## 15. Non-functional requirements
- Board with 500 tasks renders under 1.5 seconds p95.
- My tasks under 800 ms p95.

## 16. Success criteria
- **SC-001**: 30% of pilot projects run in spaces within 3 months.
- **SC-002**: No guest can reach content outside invited spaces in security tests.

## 17. Open questions
- [NEEDS CLARIFICATION: can guests be assigned tasks?] Default: yes, when invited as member.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| PRJ-001 to PRJ-003 | US1 |
| PRJ-004, PRJ-005, PRJ-010, PRJ-011 | US2, US3, US7 |
| PRJ-006 | US5 |
| PRJ-007, PRJ-008 | US4 |
| PRJ-009 | US6 |
