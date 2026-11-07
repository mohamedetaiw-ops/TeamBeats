# Implementation Plan: Learning and Mandatory Training

**Spec:** `specs/028-learning-and-training/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0013, ADR-0006, spec 009 plan (blocks), spec 011 plan (storage, scanner), spec 015 plan, spec 017 plan

## Summary
A new `Learning` module owns courses, quizzes, paths, assignments, progress and certificates. Lessons reference 009 pages and 011 files by ID through their contracts. Assignment rules are evaluated by a job that reacts to membership events and recurrence dates. SCORM packages are served from a separate content origin with a small runtime bridge that posts completion to the API through the player session.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I tenant isolation | Yes | Forced RLS; content origin paths include tenant ID; signed URLs |
| III authorization | Yes | Permissions and manager relation through `IAuthorizationService` |
| V entitlements | Yes | Section 9 keys |
| XVII connectors | Yes | LMS through a 017 learning contract |
| Rule 6 dependencies | Yes | SCORM 1.2 runtime API is a small in-house script; no new library |

## Data model (schema `learning`)
`course`, `course_version`, `lesson`, `quiz`, `question`, `path`, `path_course`, `assignment_rule`, `assignment`, `progress`, `attempt`, `certificate`, `package`

## Key decisions
- Certificates are rendered with the same PDF library used for document previews.
- Due date calculation uses the 022 working calendar contract.
- Assignment rules are idempotent: one assignment per learner, course and cycle.

## Tests
Cross-tenant certificate verification denial; scope limits on assignment; membership change assignments and withdrawals; recurrence expiry; duplicate collapse; SCORM sandbox has no session cookie; faked completion refused; LMS down path.
