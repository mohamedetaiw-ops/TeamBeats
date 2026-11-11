# Tasks: Unified Employee and Team Calendar

Depends on 009 (audience), 010 (reporting line), 017 (Google Workspace connection), 018 (calendar port), 022 (holidays and leave), 036 (design system).

## Phase 1: Specs and contracts
- [x] T001 Spec 037, plan, tasks; edits to 008 FR-051, 009 FR-018, 018 FR-017 and FR-024, 022 FR-009, 036 DLS-069 to DLS-074; module map and specs index
- [ ] T002 `IHrCalendarFeed` in `Hr.Contracts` and its implementation in `Hr` (holidays with names, leave ranges with sensitivity)
- [ ] T003 `ICalendar` occurrences carry source and iCalendar UID; Graph selects `iCalUId`

## Phase 2: Calendar module (US1 to US4, US6)
- [ ] T004 Module, migration (event, response, preference, settings), composition, module map
- [ ] T005 Aggregation service: layers, sources with states, recurrence expansion, dedup, ordering, cursor
- [ ] T006 Endpoints: events, summary, holidays, preferences, sources status, team availability, settings
- [ ] T007 Integration tests: merge and dedup, time zones and recurrence, leave cancellation, manager change, indirect reports, sensitivity, tenant isolation, source failure, preferences and settings validation

## Phase 3: Company events (US5)
- [ ] T008 Event admin endpoints with `content.publish` coverage, audience, recurrence, optimistic concurrency, cancel and delete, audit and events
- [ ] T009 RSVP endpoint and responses view; tests for audience, sibling and communicator scope

## Phase 4: Google Calendar (FR-012)
- [ ] T010 Google pack feature `calendar`, adapter, composite `ICalendar`, fake, tests

## Phase 5: Experience (US1 to US4)
- [ ] T011 Design system calendar parts and gallery section (spec 036 DLS-069 to DLS-074)
- [ ] T012 Calendar workspace page `/calendar` with agenda, week, month, team views, day panel and bottom sheet, layer chips, freshness
- [ ] T013 Home card `calendar.overview`; `outlook.my_calendar` renders it; default home uses it (spec 008 FR-051)
- [ ] T014 Admin page `/admin/calendar` for events and settings
- [ ] T015 Acceptance tests en and ar, axe, phone width

## Phase 6: AI (US7)
- [ ] T016 Tools `calendar.my_calendar` and `calendar.team_availability` in the agent tool registry

## Phase 7: Demo and staging
- [ ] T017 Staging data: distinct team meetings per manager (no look-alike repeats), demo company events, RSVP, holidays and leave for the test accounts
