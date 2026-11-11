# Implementation Plan: Unified Employee and Team Calendar

**Spec:** `specs/037-unified-calendar/spec.md` (v1.0, Approved 2026-11-11) | **Date:** 2026-11-11
**Shared base:** ADR-0013 (ports inside modules), ADR-0014 (thin frontend), spec 018 plan (calendar port), spec 022 plan (calendars and leave), spec 036 (design language system)

## Summary
A new `Calendar` module owns the unified calendar: the canonical item model, aggregation, deduplication, layers and preferences, company and department events with RSVP, calendar settings and team availability. It owns only TeamBeats events, answers, preferences and settings; every other layer is read through the owning module's contract on each request:

- Meetings: `ICalendar` of `Integrations` (spec 018 FR-024), now backed by the Outlook adapter and a new Google Calendar adapter. A composite reads both and keeps per source state.
- Holidays and leave: a new `IHrCalendarFeed` contract of `Hr` returns the employee's location holidays with names, and leave ranges per request with the sensitivity rules of spec 022 section 6 applied inside `Hr`.
- Reporting line: `IReportingLine` of `People`, read per request.
- Audience: `IAudiences` of `Content` (spec 009 model).

The frontend gets a calendar workspace page at `/calendar` and a home card component `calendar.overview`, both built from new design system calendar parts (spec 036 DLS-069 to DLS-074).

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation | Yes | New tables carry `tenant_id` with forced RLS; no tenant or user ID from the client; the composite cache key of `ICalendar` keeps tenant and membership |
| III authorization | Yes | Events use `content.publish` coverage; team data uses the reporting line per request; settings need `hr.manage` at tenant scope; no role names |
| XIV module boundaries | Yes | `Calendar` reads `Hr`, `People`, `Content`, `Integrations` only through contracts; module map updated |
| XVII connectors | Yes | Google Calendar is an adapter behind `ICalendar`; no Google type leaves `Integrations` |
| XXI entitlements | Yes | Meetings check `integration.microsoft365` or the Google connection feature; leave layers check `hr.leave`; no package names |
| XXIX configuration | Yes | Calendar settings are a versioned row validated in code against a schema |
| XXX, XXXI frontend and design | Yes | React renders server answers; layers offered come from the API; all parts from `components/ui` with `--tb-*` tokens |

## Data model (schema `calendar`)
- `calendar.event`: `tenant_id`, `id`, `org_unit_id` (null for tenant), `kind` (company, department, important), `titles jsonb`, `descriptions jsonb`, `all_day bool`, `start_at`, `end_at` (timed, UTC), `start_date`, `end_date` (all-day, end exclusive), `time_zone` (IANA), `location`, `online_url`, `audience jsonb` (spec 009 model), `recurrence jsonb` (frequency, interval, weekdays, until, count), `rsvp bool`, `status` (published, cancelled), `cancelled_at`, `created_by`, `created_at`, `updated_at`, `version`. Index on `(tenant_id, status, start_at)` and `(tenant_id, start_date)`.
- `calendar.response`: `tenant_id`, `event_id`, `occurrence_start` (instant or date at midnight UTC), `user_id`, `answer` (going, maybe, declined), `answered_at`. Primary key `(tenant_id, event_id, occurrence_start, user_id)`.
- `calendar.preference`: `tenant_id`, `user_id`, `layers text[]`, `view text`, `updated_at`.
- `calendar.settings`: `tenant_id`, `version`, `indirect_reports bool`, `threshold int`, `default_layers text[]`, `updated_by`, `updated_at`; the latest version wins.

## API (`/api/v1/calendar`, tenant user)
- `GET /events?from&to&layers&cursor` canonical items, per source state, next cursor.
- `GET /summary?from&to` per day counts by layer and team away count, alerts, layers that apply, whether the employee manages a team.
- `GET /team-availability?from&to` manager only: team size, today counts, per day available share and breach flag, people with their visible leave ranges, upcoming leave.
- `GET /holidays?from&to` the employee's holidays.
- `GET /preferences`, `PUT /preferences`.
- `GET /sources/status`.
- `POST /events/{id}/response` RSVP (body: occurrence, answer).
- `GET /admin-events`, `POST /admin-events`, `GET|PATCH|DELETE /admin-events/{id}`, `POST /admin-events/{id}/cancel`, `GET /admin-events/{id}/responses`.
- `GET|PUT /settings` (`hr.manage` at tenant scope).

## Aggregation
1. Resolve layers that apply (entitlements, connection features through `ICalendar` state, reporting line).
2. Read sources in sequence on the request scope (shared `IDbSession`), each wrapped so a failure becomes a source state, not an error.
3. Expand TeamBeats series into occurrences in the range in the event's time zone (own small expander: daily, weekly with weekdays, monthly by day; at most 100).
4. Deduplicate: key by `(source, sourceId)`, then by iCalendar UID across meeting sources and TeamBeats events (`<id>@teambeats`).
5. Order by date (all-day first), then start; page with an opaque cursor (offset over the stable order of the same range and layers).

## Google Calendar adapter
`GoogleCalendar` in `Integrations/Google` reads `calendars/primary/events?singleEvents=true&timeMin&timeMax&orderBy=startTime` as the signed-in employee's email through `GoogleSignIn` (domain-wide delegation, scope `https://www.googleapis.com/auth/calendar.events.readonly`). Feature `calendar` of the Google pack, off by default; host `www.googleapis.com` on the allowlist. A composite `ICalendar` merges Outlook and Google results, labelling each occurrence with its source and keeping both states. The fake in `DevFakes` serves the same API for tests.

## Frontend
- `features/calendar`: `CalendarWorkspace` (header, view switch, layer chips, freshness line, views), `AgendaView`, `CalendarWeek`, `CalendarMonth`, `TeamAvailability`, `DayPanel`, `ManageCalendar` (page `/calendar/manage`, open to anyone with `content.publish` for some unit or `hr.manage`).
- `components/placements/calendar.overview.tsx` the home card; `outlook.my_calendar` renders the same card so existing homes improve without a republish.
- Design system parts in `components/ui/CalendarParts.tsx` with `calendar.css`: `LayerChips`, `WeekStrip`, `AgendaRow`, `RangeMonth` (month grid with ranges), `DaySheet` (Drawer as bottom sheet on phones, side panel on wide screens), `AvailabilityHeatmap`, plus gallery section `cg-calendar`.

## Tests
- Integration: events merge and dedup, recurrence and time zone boundaries (DST, midnight, all-day exclusive end), leave cancellation disappears, pending outline status, manager change removes access, indirect reports setting, sensitive type hidden, tenant isolation of events and RSVP, sibling unit audience, communicator scope, preferences validation, settings permission, source failure keeps other layers, Google adapter through the fake.
- Unit: recurrence expander, dedup, heatmap math.
- Acceptance (Playwright): workspace views en and ar, layer toggle persists, month select opens the panel, manager team mode, home card, axe.

## Risks
- Two threads touch `Hr` contracts: the feed is a new contract file and a new class, no edit to existing leave code.
- Graph `iCalUId` is added to the `$select` list; the fake must return it.
