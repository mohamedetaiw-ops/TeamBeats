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
