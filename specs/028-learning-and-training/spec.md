# Feature Specification: Learning and Mandatory Training

| Field | Value |
|---|---|
| Spec ID | 028-learning-and-training |
| Area | knowledge |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R2 (course catalogue, courses built from pages, videos, documents and quizzes, learning paths, mandatory training assignments with due dates and reminders, completion tracking and certificates, assignments from onboarding (022) and policies (025), manager and compliance reports, external LMS connector as an alternative, SCORM 1.2 and xAPI import), R3 (AI generated quiz drafts, skill based recommendations from 010 skills, classroom sessions with attendance) |
| Last updated | 2026-11-07 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | LRN-001 to LRN-020 (new) |
| Use cases | UC-09 |
| Depends on | 002 (audiences), 004 (authorization), 005 (entitlements), 009 (content blocks, targeting), 010 (people, skills in R3), 011 (files and video storage), 015 (reminders), 017 (LMS connector), 021 (reports), 022 (onboarding tasks), 025 (policy linked training) |
| Used by | 022 (onboarding journeys), 025 (policy acknowledgement with training), 008 (My learning widget), 021 (compliance dashboards) |

## 1. Problem and outcome

**Problem:** Mandatory training (code of conduct, information security, health and safety) is tracked in spreadsheets. New joiners are sent links to an external LMS that most never open. Compliance officers cannot prove who completed what, and when a policy changes, nobody knows who must retrain.

**Outcome:** A `Learning` module lets learning administrators build short courses from content they already have, group them into paths, and assign them to audiences with due dates. Onboarding (022) and policies (025) assign training automatically. Employees see My learning on home, get reminders, and receive a certificate. Compliance officers see completion by unit and export evidence. Tenants with an LMS can keep it: the connector imports assignments and completions so TeamBeats remains the single view.

## 2. Scope

**In scope:**
- Course: lessons made of pages (009 blocks), videos and documents (011), and quizzes (single choice, multiple choice, true or false), pass mark, attempts limit, estimated duration, language versions ar and en.
- SCORM 1.2 packages and xAPI statements for imported content, played in a sandboxed frame.
- Learning paths: ordered courses with optional prerequisites.
- Catalogue: browse and search courses (012) with categories, audience targeting.
- Assignments: to people, org units, groups or roles, with due date, recurrence (for example every 12 months), and grace period; automatic assignments from 022 onboarding steps and 025 policy versions.
- Reminders before due date and escalation to the manager after it, through 015.
- Completion tracking: progress per lesson, quiz scores, completion date, certificate (PDF with tenant brand) and expiry for recurring training.
- Reports: my team (managers), unit completion and overdue (compliance officers), export to CSV, evidence pack per course.
- External LMS connector through 017: import catalogue items as links, push assignments, pull completions.

**Out of scope:**
- Authoring video, live classes and virtual classrooms (R3 for attendance only).
- Paid course marketplaces; Udemy and similar stay linked apps (016).
- Grading free-text answers.

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Learner (employee) | Take assigned and catalogue courses, see own progress and certificates | `learning.take` |
| Learning administrator | Create courses, paths, quizzes, import packages, assign to audiences in scope | `learning.manage` in org scope |
| Line manager | See direct reports' assignments and progress, assign catalogue courses to them | `learning.team_view` relation `manager_of` |
| Compliance officer | See completion and overdue reports for scope, export evidence | `learning.report` in org scope |
| Policy owner | Link a training course to a policy version (025) | `policy.manage` plus `learning.assign_linked` |

## 4. User scenarios

### User Story 1: Build and publish a course (Priority: P1, R2)

Mona builds "Information security basics" with three lessons and a 10 question quiz, pass mark 80%.

**Why this priority:** no assignments without courses.

**Independent test:** build a course from a page, a video and a quiz; publish; take it as a learner; pass and fail the quiz; check the certificate.

**Acceptance scenarios:**
1. **Given** existing pages and videos, **When** Mona adds them as lessons, **Then** they are referenced, not copied, and changes to the source page create a new course version only when she republishes.
2. **Given** a quiz with a pass mark and 3 attempts, **When** a learner fails 3 times, **Then** the course shows "attempts used" and the learning administrator can grant more.
3. **Given** a course in Arabic and English, **Then** the learner sees their language and progress is shared between versions.
4. **Given** a published course, **Then** editing creates a draft; learners who completed the old version keep their completion unless Mona marks the change "requires retraining".

### User Story 2: Assign mandatory training (Priority: P1, R2)

Acme requires the course for all of Acme Gulf every 12 months, due within 30 days.

**Acceptance scenarios:**
1. **Given** an assignment to an org unit with due in 30 days and recurrence 12 months, **Then** every member gets it, new members get it on joining with their own 30 days, and completions expire after 12 months, creating a new assignment.
2. **Given** due dates, **Then** reminders go at 7 days and 1 day before, and on overdue the learner and their manager are notified, at most once a week after that.
3. **Given** someone leaves the unit, **Then** open assignments from that unit are withdrawn, completions stay in history.
4. **Given** an assignment, **Then** it can only target people the administrator's scope covers.

### User Story 3: Learn on my own time (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** My learning, **Then** Laila sees assigned courses by due date, in-progress courses with progress, and completed ones with certificates.
2. **Given** she stops halfway, **Then** she resumes at the last lesson on any device.
3. **Given** the catalogue, **Then** she sees only courses targeted to her and can enrol herself in open ones.

### User Story 4: Onboarding and policy changes assign training (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** an onboarding journey (022) with a training step, **When** the journey starts, **Then** the course is assigned with the step's due date, and completion completes the step.
2. **Given** a policy (025) linked to a course, **When** a new major version is published that requires retraining, **Then** the audience of the policy is assigned the course, and the policy acknowledgement waits for completion if the policy owner chose that.

### User Story 5: Prove compliance (Priority: P1, R2)

Khaled, compliance officer, prepares for an audit.

**Acceptance scenarios:**
1. **Given** a course, **When** Khaled opens the report, **Then** he sees assigned, completed, overdue and expired counts per unit in his scope, with drill down to people.
2. **Given** an evidence export, **Then** it lists each person, version completed, score, completion time and certificate ID, and the export is audited.
3. **Given** a manager, **Then** they see only their direct reports.

### User Story 6: Keep our LMS (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** an LMS connection (017), **When** catalogue sync runs, **Then** LMS courses appear in the catalogue as external items that open the LMS with SSO.
2. **Given** an assignment of an external course, **Then** it is pushed to the LMS, and completions pulled back update My learning and reports.
3. **Given** the LMS is unavailable, **Then** external items show "LMS unavailable" and nothing else breaks.

### User Story 7: Import a SCORM package (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a SCORM 1.2 zip, **When** Mona imports it, **Then** it is scanned (026), unpacked to tenant storage and played in a sandboxed frame with no access to the TeamBeats session.
2. **Given** the package reports completion and score, **Then** they are recorded like native completions.

### Edge cases and exception flows
- Due date on a weekend or holiday: moved to the next working day by the learner's calendar (022).
- Learner on long leave: assignments pause when the tenant enables pause for leave.
- Course retired with open assignments: assignments are withdrawn with a notice, completions stay.
- Quiz question changed after attempts: old attempts keep their score.
- Duplicate assignment from two audiences: one assignment with the earliest due date.

## 5. Functional requirements

- **LRN-001 / FR-001**: Courses MUST be built from referenced pages, videos, documents and quizzes with versioning on publish.
- **LRN-002 / FR-002**: Quizzes MUST support single choice, multiple choice and true or false, pass marks and attempt limits.
- **LRN-003 / FR-003**: Courses MUST support ar and en versions with shared progress.
- **LRN-004 / FR-004**: Learning paths MUST order courses with optional prerequisites.
- **LRN-005 / FR-005**: Assignments MUST target people, org units, groups or roles within the assigner's scope, with due dates, recurrence and grace periods.
- **LRN-006 / FR-006**: New audience members MUST receive assignments with their own due date; leavers' open assignments MUST be withdrawn.
- **LRN-007 / FR-007**: Reminders and overdue escalations MUST go through 015 with the defaults in section 6.
- **LRN-008 / FR-008**: Recurring completions MUST expire and create a new assignment.
- **LRN-009 / FR-009**: Progress MUST resume across devices.
- **LRN-010 / FR-010**: Completion MUST produce a certificate with an ID that can be verified by authorized users.
- **LRN-011 / FR-011**: Onboarding steps (022) and policy versions (025) MUST be able to assign courses, and completion MUST be reported back to them.
- **LRN-012 / FR-012**: Republishing with "requires retraining" MUST reassign the course to people who completed earlier versions.
- **LRN-013 / FR-013**: Reports MUST show assigned, completed, overdue and expired counts per unit in the viewer's scope.
- **LRN-014 / FR-014**: Evidence exports MUST include version, score, time and certificate ID, and MUST be audited.
- **LRN-015 / FR-015**: The external LMS connector MUST import catalogue items, push assignments and pull completions through 017.
- **LRN-016 / FR-016**: SCORM 1.2 and xAPI content MUST be scanned and played in a sandboxed frame without session access.
- **LRN-017 / FR-017**: Duplicate assignments MUST collapse to one with the earliest due date.
- **LRN-018 / FR-018**: The catalogue MUST show only courses targeted to the viewer and MUST be searchable through 012.
- **LRN-019 / FR-019**: Learning administrators MUST be able to grant extra attempts, with audit.
- **LRN-020 / FR-020**: Due dates MUST respect the learner's working calendar (022).

## 6. Business and validation rules
- Reminders at 7 days and 1 day before due; overdue weekly; manager copied from overdue.
- Default grace period 0 days; range 0 to 30.
- Quiz attempts default 3; pass mark default 70%.
- SCORM package size up to 500 MB.

## 7. Tenant and organization scope
- Tenant-owned data: courses, versions, quizzes, paths, assignments, progress, attempts, certificates, packages.
- Organization scoping: administrators assign within scope; reports within scope; managers see direct reports.
- Cross-tenant denial cases: certificates verify only inside the tenant; SCORM content served from tenant paths with tenant keys.
- Sibling, parent and child cases: an Acme Gulf administrator cannot assign Acme Africa; a parent unit report includes child units.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Courses, quizzes, paths | Product | Internal | Life of course | No |
| Assignments, progress, attempts | Product (or LMS) | Confidential | 7 years for mandatory training, 2 years otherwise | Yes |
| Certificates | Product | Internal | Same as completion | Yes |
| SCORM packages | Product storage | Internal | Life of course | No |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `learning.courses` | off | on | on |
| `learning.mandatory` | off | on | on |
| `learning.scorm` | off | off | on |
| `learning.lms_connector` | off | on | on |

## 10. UX, localization and accessibility
- My learning page and widget; course player with lesson list, progress and next; course builder; assignment wizard; reports.
- Quiz questions are keyboard operable and readable by screen readers; videos need captions to publish when the tenant requires it.
- Arabic and English, RTL.

## 11. AI behavior
R2: none. R3: quiz drafts from course content through 013, always reviewed by a person before publishing.

## 12. Integrations and events
- Publishes `CourseAssigned`, `CourseCompleted`, `CourseOverdue`, `CertificateIssued`.
- Consumes `OnboardingStepStarted` (022), `PolicyVersionPublished` (025), membership changes (002).

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| learning.course_published | audit | course, version, retraining flag, actor |
| learning.assignment_created / withdrawn | audit | course, audience, due, actor |
| learning.attempts_granted | audit | learner, course, actor |
| learning.evidence_exported | audit | course, scope, actor |
| learning.progress | telemetry | course, lesson, status |

## 14. Security and abuse cases
- Malicious SCORM content: sandboxed frame on a separate origin, scanned on import.
- Faked completions: completions accepted only from the player session or the LMS connector, never from a client parameter.
- Overexposure of results: managers see direct reports only.

## 15. Non-functional requirements
- Assignment of a course to 20,000 people within 5 minutes.
- Course player first lesson under 2 seconds p95.

## 16. Success criteria
- **SC-001**: 95% on-time completion for mandatory training in pilot tenants.
- **SC-002**: A compliance officer produces an evidence export for a course in under 2 minutes.

## 17. Open questions
- [NEEDS CLARIFICATION: is SCORM 2004 needed in R2?] Default: SCORM 1.2 and xAPI only; 2004 on request.
- [NEEDS CLARIFICATION: should overdue mandatory training restrict anything?] Default: no restriction, only escalation.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| LRN-001 to LRN-004 | US1, US3 |
| LRN-005 to LRN-012 | US2, US4 |
| LRN-013, LRN-014 | US5 |
| LRN-015, LRN-016 | US6, US7 |
