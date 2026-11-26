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
| Last updated | 2026-11-26 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | PRJ-001 to PRJ-027 |
| Use cases | UC-03 |
| Depends on | 002, 004 (space roles), 005, 006 (Microsoft 365 connection), 010 (people), 011 (space document library), 012 (search), 015 (notifications), 016 (task inbox shows space tasks), 013 (AI gateway, R3), 017 (task packs, R3), 018 (Outlook calendar overlay), 022 (calendars), 023 (discussions), 026 (guest access) |
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

### User Story 8: Plan the order of work on a timeline (Priority: P1, R3)

Omar sets start dates on the ERP rollout tasks and marks which ones wait for others.

**Why this priority:** project owners need to see what slips when one task is late.

**Independent test:** link two tasks as "blocked by", move the blocker's due date past the other task's start, and check the timeline shows the conflict.

**Acceptance scenarios:**
1. **Given** a task, **When** a member says it is blocked by another task in the same space, **Then** the link is kept, both tasks show it, and a link that would make a loop is refused.
2. **Given** tasks with start and due dates, **When** anyone who can read the space opens the timeline, **Then** each task shows as a bar from start to due, grouped by milestone, with its blockers, and milestones show at their target dates.
3. **Given** a blocker whose due date is after the start of the task it blocks, **Then** the timeline and the task show a schedule conflict.
4. **Given** a task with an open blocker, **When** a member completes it, **Then** it completes and the task keeps a note that it was finished before its blocker.
5. **Given** the timeline, **Then** a keyboard user can move a task's dates with a dates form instead of dragging, and the time axis runs right to left in RTL.

### User Story 9: Track time on tasks (Priority: P2, R3)

Laila logs the hours she spent on data migration tasks.

**Acceptance scenarios:**
1. **Given** a task, **When** a member logs time with a date, minutes and an optional note, **Then** the task shows the total logged against its estimate.
2. **Given** a time entry, **Then** only its author changes or removes it, and a space owner may remove any entry.
3. **Given** a space owner, **When** they open time totals, **Then** they see minutes per person and per task for a date range, and can export them as CSV.
4. **Given** a viewer or an archived space, **Then** logging time is refused by the server.

### User Story 10: Keep Planner or Jira in step (Priority: P2, R3)

The ERP team already plans in Jira. Omar links the space's task list to the Jira project so both stay in step.

**Acceptance scenarios:**
1. **Given** a Planner or Jira connection made by an administrator in 017, **When** a space owner links one task list to a Planner plan or a Jira project, **Then** existing tasks on both sides are matched or created, and later changes flow both ways.
2. **Given** a synced task, **Then** title, description, due date, done or open, and assignees matched by work email are kept in step; other fields stay on their own side.
3. **Given** the same task changed on both sides between two syncs, **Then** the later change wins and the space shows the conflict in the sync history.
4. **Given** the external system is unavailable or refuses a change, **Then** work in the space carries on, the change is retried, and owners see the failure in the sync history.
5. **Given** an assignee who is not a member of the space, **Then** the task syncs without that assignee and the history says so.
6. **Given** the owner unlinks the list, **Then** sync stops and tasks on both sides are kept.

### User Story 11: Get a status summary (Priority: P2, R3)

Before the steering meeting Omar asks for a summary of where the project stands.

**Acceptance scenarios:**
1. **Given** a space the reader can see, **When** they ask for a status summary, **Then** a short summary is written through 013 from the tasks, milestones, overdue work and status history of that space only, and it is marked as AI generated.
2. **Given** an owner, **When** they set the project status, **Then** they can start the note from the summary and edit it before saving.
3. **Given** the AI policy blocks the request or the gateway is unavailable, **Then** the reader is told and nothing else changes.

### Edge cases and exception flows
- Last owner leaves the company: ownership passes to the space creator's manager, or the spaces administrator is asked.
- Archived space: read-only, hidden from My tasks, files kept per 011 retention.
- Task assigned to a guest whose access expired: shown as unassigned with a notice.
- Template changed: existing spaces are not affected.
- A task deleted while other tasks are blocked by it: the links are removed.
- A synced task deleted on one side: it is deleted on the other side only when the space owner allowed deletes in the link; otherwise the link for that task ends.

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
- **PRJ-019 / FR-019**: Tasks MUST support a start date and "blocked by" links to other tasks in the same space, at most 20 per task, with no loops.
- **PRJ-020 / FR-020**: A timeline view MUST show tasks by start and due date, grouped by milestone, with links and schedule conflicts, and a keyboard way to change dates.
- **PRJ-021 / FR-021**: Members MUST be able to set a task estimate and log time entries; only authors change their entries; owners may remove any.
- **PRJ-022 / FR-022**: Owners MUST see time totals per person and per task for a date range and export them as CSV.
- **PRJ-023 / FR-023**: A space owner MUST be able to link one task list to one Planner plan or one Jira project through a 017 connection; all calls go through connector adapters.
- **PRJ-024 / FR-024**: Sync MUST carry title, description, due date, done state and assignees matched by work email, both ways, with the later change winning and a history of runs, conflicts and failures.
- **PRJ-025 / FR-025**: A failed or refused external change MUST NOT block work in the space and MUST be retried.
- **PRJ-026 / FR-026**: Status summaries MUST go through 013 and use only the space the reader can see; nothing from other spaces, files or discussions is sent.
- **PRJ-027 / FR-027**: Summaries MUST be marked as AI generated, MUST NOT be saved unless an owner saves them as a status note, and MUST tell the reader when the AI policy blocks them.

## 6. Business and validation rules
- Up to 5,000 tasks per space, 500 members, 50 guests.
- Guest expiry default 90 days, range 7 to 365.
- Due reminder the day before at 09:00 in the assignee's time zone.
- R3: start date on or before due date; up to 20 blockers per task; a time entry is 1 to 1,440 minutes on one day, no later than today and no more than 365 days back; estimate up to 10,000 hours.
- R3: one sync link per task list and per external plan or project; sync every 10 minutes and on demand; history kept 90 days.
- R3: a status summary uses up to 300 tasks, the newest first, and the last 10 status entries.

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
| `spaces.task_sync` (R3) | off | on | on |

Timeline and time tracking come with `spaces.projects`. Status summaries also need `ai.assistant`. Task sync also needs `integration.connectors`.

## 10. UX, localization and accessibility
- Space directory, space overview, tasks (list and board), library, discussion, calendar, settings.
- Board drag and drop has a keyboard alternative (move to column menu); columns flow right to left in RTL.
- Arabic and English.

## 11. AI behavior
R2: none. R3: status summaries through 013 using only tasks the reader can see (US11). The space's tasks, milestones and status notes are sent as quoted data with classification Internal, or Confidential when the space is Confidential, so the 013 policy decides. The answer is shown, marked as AI generated, and kept only when an owner saves it as a status note.

## 12. Integrations and events
- Publishes `SpaceCreated`, `SpaceArchived`, `TaskAssigned`, `TaskCompleted`, `MilestoneReached`, `SpaceStatusChanged`.
- Consumes leave events (022), guest expiry (026), people changes (010).
- R3: Planner (through the 006 Microsoft 365 connection) and Jira Software (Jira Cloud REST) task packs in 017. Only canonical task fields cross the contract.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| spaces.created / archived / restored | audit | space, type, visibility, actor |
| spaces.member_added / removed | audit | space, member, role, actor |
| spaces.guest_invited / expired | audit | space, guest, actor |
| spaces.task_activity | telemetry | space, action (no content) |
| spaces.sync_linked / unlinked | audit | space, list, system, actor |
| spaces.status_summarized | audit | space, outcome (no content) |

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
| PRJ-019, PRJ-020 | US8 |
| PRJ-021, PRJ-022 | US9 |
| PRJ-023 to PRJ-025 | US10 |
| PRJ-026, PRJ-027 | US11 |
