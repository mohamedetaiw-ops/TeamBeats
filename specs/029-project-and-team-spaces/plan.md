# Implementation Plan: Project and Team Spaces

**Spec:** `specs/029-project-and-team-spaces/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0013, ADR-0006, spec 011 plan (libraries), spec 023 plan (channels), spec 026 plan (guests), spec 016 plan (inbox)

## Summary
A new `Spaces` module owns spaces, members, tasks, milestones, statuses and templates. On space creation it asks 011 for a library and 023 for a channel through their contracts, passing the space as the audience provider. Space roles are relations in 004. My tasks is a query in `Spaces` that the 016 inbox shows as a tab through a registered task source.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I tenant isolation | Yes | Forced RLS; guests are tenant-bound identities |
| III authorization | Yes | Relations owner, member, viewer in 004 |
| V entitlements | Yes | Section 9 keys and limit |
| Rule 5 | Yes | Library and channel via contracts; leave via 022 events |
| Rule 11 frontend | Yes | Board is a registered component; no rules in React |

## Data model (schema `spaces`)
`space`, `space_member`, `task_list`, `task_column`, `task`, `task_assignee`, `checklist_item`, `milestone`, `milestone_task`, `status_entry`, `space_template`, `space_event`

## Tests
Visibility across surfaces for private and secret spaces; guest boundary tests; viewer edit refusal; classified file gate for guests; cross-tenant guest denial; ownerless reassignment; inbox tab; performance.

## R3 plan (2026-11-26)

**Scope:** US8 to US11, PRJ-019 to PRJ-027.

**Dependencies and timeline (US8):** `spaces.task` gains `start_on` and `estimate_minutes`. A new `spaces.task_link (blocked_task_id, blocker_task_id)` table holds "blocked by" links inside one space. A loop check walks the links before insert. `GET /spaces/{id}/timeline` returns tasks with dates, links, milestones and conflicts, where a conflict is a blocker due after the start of the task it blocks. The web timeline is a bar grid built from design system tokens. A dates form is the keyboard path, and the axis follows the document direction. Completing a task with an open blocker is allowed and recorded as `finished_before_blocker`, which is the smallest safe default because the spec does not make blockers hard stops.

**Time tracking (US9):** `spaces.time_entry (task_id, user_id, worked_on, minutes, note)`. The routes are `POST/PUT/DELETE /spaces/{id}/tasks/{taskId}/time`, `GET /spaces/{id}/time?from&to` (owners only) and its CSV export. Members log time, authors edit their own entries, and owners may remove any entry. Archived spaces refuse.

**Task sync (US10):** A new contract in Integrations.Contracts, `ITaskTrackers`, has `ListAsync(connection, container, since)`, `CreateAsync`, `UpdateAsync`, `DeleteAsync` and `ContainersAsync` over canonical `TrackerTask` records. There are two packs:
- `planner` calls Microsoft Graph Planner with an app token through the tenant's 006 Microsoft 365 connection.
- `jira_software` calls Jira Cloud REST v3 with basic or bearer sign in through `RestSignIn`.

Spaces owns the `spaces.sync_link` and `spaces.sync_item` tables (task to external id, last seen versions) and `spaces.sync_run` (history). `SpacesMaintenanceWorker` pulls every 10 minutes. Pushes are durable connector jobs keyed by task and row version, so a retry sends a change once. On conflict the later change wins using the external modified time against `task.updated_at`. Tests use fake HTTP handlers for both packs.

**Status summaries (US11):** `POST /spaces/{id}/status-summary` calls the AI gateway with use case Summarize, the space's tasks (up to 300), milestones and the last 10 status entries as quoted data. The classification is Internal, or Confidential when the space is. The outcomes are summarized, policy_blocked and unavailable. The summary is not stored. The owner's status form can start from it.

**Entitlements:** migration adds `spaces.task_sync` (Plus and above). Summaries need `ai.assistant`.

**Audit:** `spaces.sync_linked`, `spaces.sync_unlinked`, `spaces.status_summarized`.

**Tests:** Integration tests cover:
- loops and the 20 link limit
- timeline conflicts and viewer refusal
- time entry author rules, owner totals and the archived refusal
- sync both ways with conflict, refusal and retry, unknown assignee, unlink, and the entitlement off
- summaries sending only the reader's space, with policy block and unavailable outcomes
- cross-tenant denial

e2e covers the timeline, time logging, the sync link page with the fake tracker, and the summary.
