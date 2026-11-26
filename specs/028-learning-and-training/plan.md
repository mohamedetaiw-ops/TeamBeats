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

## R3 plan (2026-11-26)

**Scope:** US8 to US10, LRN-021 to LRN-030.

### Quiz drafts (US8)
- `QuizDrafts` in Learning: reads the text of the draft's page lessons through `IPublishedPages` (as the administrator) and, for document lessons, the extracted text of controlled documents through `IDocumentReferences.ControlledAsync`. Text is cut to 60,000 characters and sent as quoted data through `IAiGateway.ChatAsync` with use case `draft`, classification Internal and a JSON response schema.
- The answer is parsed against the quiz shape (`CourseModel` rules: kinds, 2 to 10 options, at least one correct, one for single choice, two for true or false, text in every course language); anything that does not fit is dropped. Kept questions are added to the named quiz lesson, or to a new quiz lesson at the end, with `aiDraft: true`, as a new draft revision (If-Match on the revision like the builder).
- `CourseModel.CheckPublishable` refuses `validation.learning.quiz_unreviewed` while any question is marked. The builder shows the mark with Keep and Remove.
- Audit `learning.quiz_drafted` with the count.

### Skill recommendations (US9)
- People contract `IPersonSkills`: `ListedAsync` (the tenant's listed skills, null when off or unavailable) and `DeclaredAsync(userId)` (skill IDs with proficiency, null when off). Learning never reads People tables.
- `learning.course.skills uuid[]` (migration 0006), set in the builder with the other unversioned settings; unknown or unlisted skills are dropped.
- `Recommendations`: published, internal, open enrolment, targeted to the learner, not open or completed by them, not dismissed, tagged with a declared skill below expert; ranked by matching skills, basic before good, newest first; at most six. `learning.recommendation_dismissal` table.

### Classroom sessions (US10)
- Lesson kind `classroom` (no sources). Migration 0006 adds `learning.session` (course, lesson key, start, end, place, link, capacity, facilitator, status) and `learning.seat` (session, assignment, learner, status booked, waiting, cancelled, attended, absent; one live seat per learner and lesson by a partial unique index).
- Capability `learning.classroom` (Entitlements migration 0054, Plus and Enterprise). Publishing a course with a classroom lesson and changing sessions demand it.
- Booking locks the session row; full sessions add to the waiting list in order. Cancelling promotes the first waiting seat with a notification. Attendance writes `lesson_progress` through the player's completion path, so course completion, certificates and obligations follow as for any lesson.
- `LearningMaintenanceWorker` sends `learning.session.reminder` once per seat the day before (a `reminded_at` column).
- Pages: sessions section on a classroom lesson in the builder, seat booking in the player, and a facilitator page `/learning/sessions/{id}` with the roster and attendance.

### Tests
Quiz draft added and marked, publish refused until reviewed, refusal changes nothing, unreadable lessons not sent, other tenant not found; recommendations by skill, dismissal, skills off; seats, waiting list promotion, reminder once, attendance completes the course, absent may rebook, before start refused, learner cannot record attendance, capability denial on Workplace, other tenant denied.
