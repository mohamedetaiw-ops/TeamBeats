# Feature Specification: Unified Employee and Team Calendar

| Field | Value |
|---|---|
| Spec ID | 037-unified-calendar |
| Area | experience |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R2 (calendar workspace with agenda, week, month and team availability views, home calendar card, layers and preferences, company and department events with audience and RSVP, location holidays, my leave and team leave, team availability heatmap, Outlook and Google Calendar meetings through the same port, deduplication, calendar tools for the assistant), R3 (event registration with capacity and waitlist through spec 009 event items, reminders through spec 015, attachments, shift rosters and part-time patterns from spec 022 R3, provisional and observed holidays, background sync of external calendars if spec 018 section 6 changes) |
| Last updated | 2026-11-14 |
| Approved | 2026-11-11 under Mohamed's standing approval of 2026-11-07 (request of 2026-11-11 in the thread "Build the unified team calendar") |
| Requirement IDs | CAL-001 to CAL-032 (new); INT-002 (calendar part), UC-10 (calendar part), LVA-001 and LVA-009 (calendar part) |
| Use cases | UC-10 |
| Depends on | 002 (org units), 004 (authorization), 005 (entitlements), 006 (audit), 007 (localization, Hijri display), 008 (home and shell), 009 (audience model, event content), 010 (reporting line and profiles), 013 and 014 (assistant), 015 (notifications), 017 (Google Workspace connection), 018 (Outlook calendar port), 022 (holidays, working week, leave), 027 (agent tools), 032 (component manifests), 036 (design language system) |

## 1. Problem and outcome

**Problem:** The home calendar is a long list of Outlook meetings. It repeats meetings that look the same, has no views, no navigation and no hierarchy, and shows nothing else that decides an employee's week: company events, public holidays, their own leave and who in their team is away. Managers have to open the leave page to see who is off, and nobody can plan a week or a month from one place.

**Outcome:** One calendar brings meetings, company and department events, public holidays, the employee's own leave and, for managers, team availability into one normalized view. The home page shows a light card (the week, today's next items and alerts) that opens a calendar workspace with agenda, week, month and team availability modes. The server decides every item an employee may see; the browser never filters other people's calendars. The same permission checked API serves the page, the home card and the assistant.

## 2. Scope

**In scope:**
- A canonical calendar item model and one API that merges every source by layer, in a bounded date range, with the actions each item allows.
- Layers: meetings (Outlook, Google Calendar), company events, department events, important dates, public holidays, my leave, team leave. Layers turn on and off per employee without changing any data.
- Calendar workspace page with agenda, week, month and team availability views, a Today shortcut, previous and next, a date picker, layer filters and a source freshness line.
- Home calendar card: the week strip with markers, today's next items, holiday and team leave alerts, and a link to the workspace.
- Company and department events published by communicators, with audience targeting (spec 009 audience model), recurrence, online link, location and RSVP.
- Holidays from the employee's location working calendar (spec 022).
- My leave as date ranges with approved and pending status, from the leave module (spec 022), which stays the system of record.
- Team availability for managers: their reports' leave as ranges, a heatmap of the share of the team available per day with a configurable staffing threshold, and upcoming leave.
- Deduplication of the same meeting seen through two sources or two calendars.
- Calendar read tools for the assistant that call the same service as the page.
- Calendar settings per tenant: indirect reports in team availability, staffing threshold, default layers.

**Out of scope:**
- A second leave approval process. Leave is requested and approved only in spec 022; the calendar links there.
- Creating, editing or answering Outlook or Google meetings from TeamBeats (spec 018 FR-015 keeps this in R3).
- Storing the content of external meetings. Spec 018 section 6 keeps Outlook content out of TeamBeats storage; the same rule applies to Google Calendar. Background sync with change notifications is listed for R3 and needs that rule to change first (section 17).
- Showing a colleague's leave reasons, leave types marked sensitive, or private meeting subjects to anyone who may not see them.
- Room booking (spec 018 US5, spec 031).

## 3. Actors and permissions

| Actor | What they can do in this feature | Permission / scope required |
|---|---|---|
| Employee | See their own calendar layers, choose views and layers, answer company events | Signed-in tenant member |
| Line manager | Everything an employee can, plus team availability and team leave for their reports | Reporting line (spec 010), checked on every request |
| Communicator | Create, change, cancel and delete company and department events for an org subtree, and see responses | `content.publish` at the event's org unit (spec 004) |
| HR officer | Manage holiday calendars and their assignment to locations (spec 022), calendar settings | `hr.calendar_manage` (holidays), `hr.manage` at tenant scope (settings) |
| Tenant administrator | Same as HR officer for settings; turn the Outlook and Google calendar features on (specs 006, 017, 018) | `integration.manage` for connections |
| Assistant | Read the employee's calendar and, for managers, team availability, through tools | The employee's own access only (spec 027) |

No new permission key: events reuse `content.publish`, holidays reuse `hr.calendar_manage`, settings reuse `hr.manage`. Roles are never checked by name.

## 4. User scenarios

### User Story 1: See my week at a glance on the home page (Priority: P1, R2)

Leila opens TeamBeats on her phone. The calendar card shows this week as seven days with small markers under each day for meetings, company events, leave and holidays. Today shows three activities: Leadership weekly at 9:00 in Al Noor Boardroom, the company town hall at 12:00 online, and "2 team members on leave". She taps "Open calendar" to plan the rest of her week.

**Why this priority:** the home card is what most employees see every day, and today it is the weakest part of the home page.

**Independent test:** with meetings, a company event, a holiday and leave in the week, the card shows the week strip with markers by layer, today's items in time order without repeats, and opens the workspace.

**Acceptance scenarios:**
1. **Given** an employee with meetings, an event, a holiday and approved leave this week, **When** the home page loads, **Then** the card shows the seven days of the week with a marker per layer that has items, with a text legend, and today's next items (at most 3) with time, title, place and a source cue. *(CAL-001)*
2. **Given** the same meeting appears in the employee's Outlook calendar and as an invitation copy, or through Outlook and Google, **Then** it is shown once. Two different meetings with the same title and time are both shown. *(CAL-012)*
3. **Given** a manager whose reports have leave today, **Then** the card shows one line "N team members on leave" that opens team availability, not one item per person. *(CAL-002)*
4. **Given** a holiday in the next 14 days, **Then** the card shows it as an alert line with its name and date. *(CAL-002)*
5. **Given** Outlook is not connected, **Then** the card still shows events, holidays and leave and offers Connect Outlook in one line; it never shows an empty card because one source is missing. *(CAL-020)*

### User Story 2: Plan with agenda, week and month views (Priority: P1, R2)

Omar opens the calendar workspace. The agenda lists the next days grouped by date with compact rows. He switches to Month, sees his approved leave from 12 to 16 November as one continuous bar, a pending day as an outlined bar, and the National Day holiday marked on its own. He selects the 18th; a panel opens with that day's items. On his phone the same panel opens as a bottom sheet.

**Why this priority:** planning a week or a month is the reason to open a calendar at all.

**Independent test:** switch between agenda, week and month; move with previous, next, Today and the date picker; select a date and read its items; repeat in Arabic and on a phone width.

**Acceptance scenarios:**
1. **Given** the workspace, **Then** it offers Agenda, Week and Month (and Team for managers) as a segmented control, previous and next, Today and a date picker; the view and the date are kept in the address so the page can be shared and reloaded. *(CAL-003)*
2. **Given** the agenda view, **Then** items are grouped by date with today first, each row shows the time or "All day", the title, place or "Online", a layer colour and icon, and expands to its details and actions (Join, Open in Outlook or Google, RSVP, Request leave, View team availability). *(CAL-004)*
3. **Given** the week view, **Then** seven days show side by side with all-day items on top and timed items by time; on a phone the week shows as a day strip with the selected day's agenda below. *(CAL-005)*
4. **Given** the month view, **Then** leave and holidays spanning several days show as continuous date ranges, pending leave is outlined and patterned, holidays are marked apart from leave, and meetings show as compact markers with a count. *(CAL-006)*
5. **Given** a date is selected in the month or week view, **Then** a panel beside the calendar on wide screens, or a bottom sheet on phones, lists that day's items. *(CAL-007)*
6. **Given** a screen reader, **Then** every marker and range has a text label (layer, status, dates), the month is a table with day headers, and meaning never depends on colour alone. *(CAL-008)*
7. **Given** Arabic, **Then** the calendar mirrors, weekday names and numbers follow the locale, and the tenant's Hijri display setting applies (spec 007). *(CAL-009)*

### User Story 3: Choose my layers (Priority: P1, R2)

Leila hides department events and keeps meetings, company events, holidays and leave. The next day on her laptop the same layers are off.

**Independent test:** turn a layer off, reload on another browser, see the same layers; data in other views is unchanged.

**Acceptance scenarios:**
1. **Given** layer filters, **When** an employee turns a layer off, **Then** its items and markers disappear from every view and the card, and the choice is saved to their preferences on the server and applies on every device. *(CAL-010)*
2. **Given** a layer the employee cannot have (no Outlook connection feature, no `hr.leave`, not a manager), **Then** the layer is not offered. The server returns the layers that apply; the page never decides from roles or packages. *(CAL-011)*
3. **Given** the view (agenda, week or month), **Then** the last used view is saved as a preference too. *(CAL-010)*

### User Story 4: See my team's availability (Priority: P1, R2)

Sara manages ten people. In Team mode she sees 8 of 10 available today, 2 on leave, 1 starting leave soon, a heatmap of the month where Thursday the 19th shows 60% and a warning because it is below the tenant's 70% threshold, and the upcoming leave list: Sarah Ahmed 12 to 16 November approved, Omar Hassan 18 to 20 November approved, Mona Ali 23 to 25 November pending.

**Why this priority:** managers approve leave and plan work; one view of team availability is worth more than ten leave entries.

**Independent test:** a manager with reports on approved and pending leave sees counts, the heatmap with the threshold flag and the list; a non-manager does not get the view; a sensitive leave type shows only "On leave".

**Acceptance scenarios:**
1. **Given** a manager, **When** they open Team, **Then** they see, for the period, the number of reports available today, on leave today and with leave starting in the next 7 days, a heatmap with the share of the team available on each working day, and upcoming leave with status. *(CAL-013)*
2. **Given** 4 of 10 reports are away on a day, **Then** the heatmap shows 60% for that day and flags it when it is below the tenant's staffing threshold (default 70%). Non-working days of the team's calendar are not scored. *(CAL-014)*
3. **Given** a report's leave type is sensitive (spec 022 section 6), **Then** the manager sees the type only where spec 022 allows it, never a reason, comment or document; anyone else sees only "On leave". *(CAL-015)*
4. **Given** an employee with no reports, **Then** the Team mode and the team leave layer are not offered and the team availability API answers not found. *(CAL-016)*
5. **Given** a report moves to another manager, **Then** the old manager loses their leave on the next request, even when a cached answer exists; the reporting line is checked on every request. *(CAL-017)*
6. **Given** the tenant turns on indirect reports, **Then** managers also see their reports' reports; by default they see direct reports only. *(CAL-018)*

### User Story 5: Publish a company event (Priority: P2, R2)

Huda from internal communications creates "Company town hall" for 12 November 12:00 to 13:00, online with a Teams link, for the whole tenant, with RSVP. A department head creates a monthly "Finance all hands" for the Finance subtree. Employees in the audience see them on their calendar and answer Going, Maybe or Not going; Huda sees the counts.

**Independent test:** create an event for a subtree; a member sees it and answers; a sibling unit member does not see it; a communicator scoped to another unit cannot change it.

**Acceptance scenarios:**
1. **Given** `content.publish` at a unit, **When** a communicator creates an event owned by that unit, **Then** they set title and description in each tenant language, kind (company event, department event, important date), start and end with time zone or all-day dates, location, online link, audience (spec 009 model within the unit's subtree), recurrence (daily, weekly on chosen days or monthly, with an end date or a count, at most 100 occurrences) and whether employees can RSVP. *(CAL-021)*
2. **Given** an event, **Then** only people the audience matches see it, every time the calendar is read; targeting is evaluated on the server. *(CAL-022)*
3. **Given** RSVP is on, **When** an employee answers, **Then** their answer is saved per event (or per occurrence of a series), they can change it until the event ends, and the communicator sees counts but not who answered unless they hold `content.publish` on the owning unit. *(CAL-023)*
4. **Given** an event is cancelled, **Then** it shows as cancelled (struck through with the word "Cancelled") for 7 days and then disappears; a deleted event disappears at once. *(CAL-024)*
5. **Given** a communicator of the Egypt subtree, **Then** they cannot see, change or target events of the Gulf subtree. *(CAL-025)*

### User Story 6: Know when data is old (Priority: P2, R2)

Microsoft 365 is slow. The calendar still loads company events, holidays and leave, and says "Outlook: last updated 10:42" with a retry.

**Acceptance scenarios:**
1. **Given** a source fails, **Then** the other layers still load, the failed source shows its state and last successful time, and the calendar never presents stale availability as current. *(CAL-020)*
2. **Given** the sources status endpoint, **Then** it lists each source the employee has (Outlook, Google, events, holidays, leave) with state and as-of time. *(CAL-019)*

### User Story 7: Ask about my calendar (Priority: P2, R2)

Leila asks the assistant "What is on today?" and Sara asks "Which days next month have the highest team availability?". The assistant answers from the same calendar service with the employee's own access.

**Acceptance scenarios:**
1. **Given** the calendar tools are allowed for the tenant (spec 027), **Then** the assistant can list the employee's merged calendar for up to 31 days and, for a manager, team availability aggregates for up to 62 days, with the same filtering as the page; it never receives another employee's private subject, leave type or reason it could not see on the page. *(CAL-026)*
2. **Given** a person who is not a manager, **Then** the team tool answers that there is no team, not an error with data. *(CAL-026)*

### Edge cases and exception flows
- A meeting that crosses midnight shows on both days in week and month views and once in the agenda, on its start day, with its end date.
- An all-day item keeps its local dates in every time zone: dates are stored as calendar dates with an exclusive end, never as instants.
- A recurring event over a daylight saving change keeps its local start time.
- A range longer than the allowed window (62 days for events and team availability, 366 for holidays) is refused with a validation message.
- More than 500 items in a range: the API pages with a cursor; the page asks for more as the employee scrolls the agenda.
- An employee with no primary unit gets the tenant default working calendar and its holidays.
- A manager with more than 200 reports in scope: aggregates count everyone, the list shows the first 200 by start date and says how many more there are.
- Leave cancelled after being shown: it disappears on the next read; nothing is cached across requests for leave.
- Two Outlook occurrences of a series on the same day are different occurrences, not duplicates.
- A company event that a communicator also sent as an Outlook invitation is shown once, as the company event, with Join from the Outlook copy when it has one.

## 5. Functional requirements

### Model and API
- **FR-001 / CAL-001**: The system MUST normalize every source into one calendar item: tenant, an item ID, source system (outlook, google, events, holidays, leave), source item ID, layer (meeting, company, department, important, holiday, my_leave, team_leave), title, start and end (UTC instants for timed items, local dates with an exclusive end for all-day items), time zone, all-day flag, visibility (private, team, unit, tenant), status (confirmed, tentative, pending, cancelled), owner (a person or unit reference), recurrence (for TeamBeats series), source update time, the actions the employee may take, and a small set of source fields (location, online link, web link, free or tentative markers, RSVP answer and counts).
- **FR-002 / CAL-002**: The system MUST provide a summary per day for a range (count per layer and team members away) and the alerts the home card shows (holidays in the next 14 days, team members away today).
- **FR-003 / CAL-003**: The events API MUST take a bounded range (at most 62 days), layers and a cursor, return at most 500 items a page ordered by date, all-day first, then start, and refuse a range outside the bounds.
- **FR-004 / CAL-012**: The system MUST deduplicate by source identity: the same provider item once; the same meeting across Outlook and Google, or across calendars, by its iCalendar UID; a TeamBeats event and its Outlook or Google invitation copy by the UID TeamBeats gives the event. Titles and times MUST NOT be used to merge items.
- **FR-005 / CAL-019**: The system MUST report the state and as-of time of each source the employee has, and every events answer MUST carry the same per source state.

### Views
- **FR-006 / CAL-003 to CAL-009**: The workspace MUST offer agenda, week and month views with Today, previous, next and a date picker, and keep the view and date in the address. Week and month MUST show multi-day leave and holidays as continuous ranges, pending leave outlined and patterned, holidays apart from leave, and several markers on one date. Selecting a date MUST open a panel (wide screens) or bottom sheet (phones) for that day. Everything MUST work by keyboard and screen reader, mirror in Arabic and follow spec 036.
- **FR-007 / CAL-001, CAL-002**: The home calendar card (`calendar.overview`) MUST stay light: the current week with markers, today's next items (at most 3), alerts, and a link to the workspace. It replaces My calendar in the default home of spec 008 FR-051 and needs no capability.
- **FR-008 / CAL-004**: Rows MUST offer only the actions the API returns for the item: join (from 10 minutes before an online meeting until it ends), open in the source, RSVP, request leave (opens spec 022), view team availability.

### Layers and preferences
- **FR-009 / CAL-010**: Layer choices and the last view MUST be saved per employee on the server, validated against the layers the employee has, and apply on every device. Turning a layer off MUST NOT change source data.
- **FR-010 / CAL-011**: The API MUST return the layers that apply to the employee: meetings when an Outlook or Google calendar feature is on and the employee can use it; my leave when `hr.leave` is on; team leave and team availability when the employee has reports in scope; company, department and important dates and holidays always.

### Sources
- **FR-011 / CAL-012**: Meetings MUST come through the canonical calendar port of spec 018 FR-024 (`ICalendar`), from the Outlook adapter and the Google Calendar adapter, read as the employee. Cancelled and declined meetings are hidden; private subjects are hidden when the home placement asks (spec 018 FR-016).
- **FR-012 / CAL-012**: The Google Calendar adapter MUST be a feature of the tenant's Google Workspace connection (spec 017), off by default, asking only for `calendar.events.readonly`, and MUST read only the calendar of the signed-in employee's own address, never another person's.
- **FR-013**: Holidays MUST come from the working calendar of the employee's location (spec 022 FR-007), with the holiday name in each language and half days marked, through the `Hr` holiday contract, never from the device location.
- **FR-014**: My leave MUST come from spec 022 as date ranges per request with status approved or pending; cancelled and rejected leave is not shown. The calendar MUST NOT create or approve leave.
- **FR-015 / CAL-013 to CAL-018**: Team leave and team availability MUST be computed on the server from the reporting line (spec 010) checked on every request, the leave module's ranges and each person's working calendar. Indirect reports MUST follow the tenant setting (default off). The answer MUST return aggregates for everyone in scope and individual ranges only for people the manager may see.
- **FR-016 / CAL-015**: Leave types MUST follow spec 022 section 6 sensitivity; reasons, comments and documents MUST never reach the calendar.

### Company events
- **FR-017 / CAL-021, CAL-025**: Holders of `content.publish` at a unit MUST be able to create, change, cancel and delete events owned by that unit or a unit below it, with titles in each tenant language, kind, timing, location, online link, audience within the owning subtree, recurrence and RSVP. Changes MUST use optimistic concurrency.
- **FR-018 / CAL-022**: Event visibility MUST be decided on the server with the spec 009 audience model for every read.
- **FR-019 / CAL-023**: RSVP answers (going, maybe, declined) MUST be stored per employee and occurrence, changeable until the occurrence ends; counts are visible to the communicator, names only to `content.publish` holders on the owning unit.
- **FR-020 / CAL-024**: Cancelled events MUST show as cancelled for 7 days after cancellation, then stop showing; deleted events stop at once.

### Settings
- **FR-021 / CAL-014, CAL-018**: Calendar settings MUST be versioned tenant configuration validated against a schema: indirect reports in team availability (default off), staffing threshold in percent (default 70, 10 to 100), default layers for new employees. Only `hr.manage` at tenant scope may change them.

### AI
- **FR-022 / CAL-026**: The assistant MUST read the calendar only through registered tools (`calendar.my_calendar`, `calendar.team_availability`) that call the same service as the page with the employee's identity, under spec 027 rules, with item text passed as untrusted content.

### Operations
- **FR-023 / CAL-020**: When a source fails the other layers MUST load, and the failed source MUST show its state and the time of its last good read; stale availability MUST NOT be presented as current.
- **FR-024**: The events and summary answers for holidays and settings MAY be cached per request; leave and reporting line answers MUST NOT be cached across requests.
- **FR-025**: Calendar views MUST follow spec 036 DLS-119: an item spanning several days appears once in the agenda with its date range; overlapping items in the week view sit side by side and beyond three share a "+N" block that opens the day panel; a month cell shows at most three items and then "+N more"; a calendar page shows one agenda for the selected range and no second list under a grid. The same item reads the same in every view, and a title in another script starts at the same edge as the others.
- **FR-026**: The layer controls MUST read as one group: chips with one neutral outline, the layer's colour as a marker, its icon and its label, pressed when the layer is on (spec 036 DLS-070). The week view MUST show the current time line and the working hours of the person's working calendar.
- **FR-027**: The home calendar component MUST be the summary variant: today's remaining items and the next one after them, at most five rows, with "Open the calendar". It MUST NOT embed the full view switch or the layer legend.

## 6. Business and validation rules
- Ranges: events and summary up to 62 days, team availability up to 62 days, holidays up to 366 days.
- Events: title 1 to 200 characters per language, description up to 4,000, location up to 200, online link https only, end after start, all-day end date after start date, at most 100 occurrences, at most 5 years ahead.
- RSVP: allowed only when the event has RSVP on, the employee is in the audience and the occurrence has not ended.
- Preferences: layers from the known list only; unknown layers are refused.
- Team availability: a person counts as away on a day with approved leave, half away with half a day; pending leave is shown but not counted as away. Working days come from each person's calendar.
- Threshold breach: available share strictly below the threshold.

## 7. Tenant and organization scope
- Tenant-owned data: events, occurrences answers, preferences, settings. Every table carries `tenant_id` with forced row level security.
- Organization scoping: an event belongs to an org unit; its audience stays inside that unit's subtree; a whole tenant event belongs to the tenant (owning unit empty) and needs `content.publish` at tenant scope.
- Cross-tenant denial cases: an event ID, RSVP or team availability call from another tenant answers not found; a manager link that crosses tenants does not exist because identities are per tenant (ADR-0005).
- Sibling, parent and child boundary cases: a communicator of a child unit cannot manage a parent's event; a sibling unit's members do not see a subtree event; a parent communicator manages child unit events with subtree scope.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Company, department events and important dates | TeamBeats `Calendar` | Internal | Until deleted; cancelled events kept 1 year for audit | No (owner reference) |
| RSVP answers | TeamBeats `Calendar` | Internal | 1 year after the occurrence | Yes |
| Calendar preferences | TeamBeats `Calendar` | Internal | While the membership exists | Yes |
| Calendar settings | TeamBeats `Calendar` | Internal | Versions kept | No |
| Meetings | Microsoft 365 or Google | Confidential | Not stored; in-memory cache up to 5 minutes per tenant and membership (spec 018) | Yes |
| Holidays | TeamBeats `Hr` | Internal | Spec 022 | No |
| Leave ranges | TeamBeats `Hr` or the HR system (spec 019) | Confidential | Spec 022; not stored by the calendar | Yes |

## 9. Package entitlements
- No new capability key. The calendar workspace, the card, events and holidays are part of every package.
- Meetings need `integration.microsoft365` (Outlook) or the Google Workspace connection feature (spec 017). Leave layers and team availability need `hr.leave` (spec 022). Tools also need the AI keys of specs 013 and 020.

## 10. UX, localization and accessibility
- Built only from the design system (spec 036 DLS-069 to DLS-074): calendar header, layer chips, week strip, agenda rows, month ranges, day panel and bottom sheet, availability stats and heatmap.
- Layer colours come from chart tokens with a distinct icon per layer, a text legend and a pattern for pending, so meaning never depends on colour alone.
- Times in the tenant time zone and locale, numbers and weekday names localized, mirrored in Arabic, Hijri display when the tenant turns it on (spec 007).
- Phone: agenda is the default view, the week shows as a day strip, a selected day opens a bottom sheet. Tablet and desktop: the day panel sits beside the calendar.

## 11. AI behavior and data policy
- Calendar tools read only what the employee could see on the page, return meeting text as untrusted content, and are Confidential (spec 013 routing).
- Team tools return aggregates and the leave ranges the manager may see; never types the manager may not see, and never reasons.

## 12. Integrations and events
- Consumes: `ICalendar` (spec 018 port, Outlook and Google adapters), `IHrCalendarFeed` (spec 022 holidays and leave ranges), `IReportingLine` (spec 010), `IAudiences` (spec 009), `IWorkingCalendar` (spec 022).
- Publishes: `CalendarEventPublished`, `CalendarEventCancelled`, `CalendarEventResponded` (IDs only), for notifications (spec 015) and analytics (spec 021).
- Each TeamBeats event has an iCalendar UID `<event id>@teambeats` so invitation copies merge (FR-004).

## 13. Audit, telemetry and analytics

| Event | Type | Data |
|---|---|---|
| calendar.event_created, calendar.event_changed, calendar.event_cancelled, calendar.event_deleted | audit | event, unit, actor, changed fields |
| calendar.settings_changed | audit | version, changed fields, actor |
| calendar.team_viewed | audit | manager, range, number of people (sensitive leave access, constitution audit rule) |
| calendar.source_failed | telemetry | source, error class |

## 14. Security and abuse cases
- Tenant from the session only; all IDs scoped to the tenant.
- No client side filtering of other people's data: the API returns only authorized items and fields.
- Manager rights revalidated per request; no cached reporting line.
- Online links: https only; rendered with rel noopener noreferrer.
- Range and page limits stop bulk extraction; team availability limited to people in scope.

## 15. Non-functional requirements
- Events answer p95 under 800 ms with Outlook cached, under 2 s when Outlook is read; summary under 500 ms.
- The card renders within the home page budget and never blocks other components.

## 16. Success criteria
- Employees see meetings, events, holidays and their leave together without duplicates.
- Managers see team availability without access to confidential HR data.
- Layers and the view persist across devices.
- Communicators publish events for their subtree only.
- A source outage leaves the other layers working with a visible last updated time.
- Automated tests cover overlapping events, recurring events, time zone boundaries, leave cancellation, tenant isolation and a manager change.

## 17. Open questions
- External meetings are read live through the employee's delegated access with the 5 minute cache of spec 018. Storing a synced copy with cursors and change notifications, as proposed in the request, would change spec 018 section 6 ("not stored") and needs Mohamed's decision. Default: keep live reads; revisit in R3.
- Provisional and observed holidays need a field on spec 022 holidays. Default: not shown until spec 022 adds it.
- Colleague (non-report) availability beyond free and busy on profiles (spec 018 FR-018). Default: not in the calendar.

## 18. Traceability

| Requirement | Covered by |
|---|---|
| CAL-001, CAL-002 | FR-001, FR-002, FR-007, US1 |
| CAL-003 to CAL-009 | FR-003, FR-006, US2 |
| CAL-010, CAL-011 | FR-009, FR-010, US3 |
| CAL-012 | FR-004, FR-011, FR-012 |
| CAL-013 to CAL-018 | FR-015, FR-016, FR-021, US4 |
| CAL-019, CAL-020 | FR-005, FR-023, US6 |
| CAL-021 to CAL-025 | FR-017 to FR-020, US5 |
| CAL-026 | FR-022, US7 |
| CAL-027 to CAL-032 | Reserved for R3 (registration, reminders, attachments, rosters, provisional holidays, background sync) |

## Review checklist
- [x] No [NEEDS CLARIFICATION] left; open questions have safe defaults
- [x] Tenant and org scope covered with denial cases
- [x] Permissions use keys, no role names
- [x] Entitlements use capability keys
- [x] Audit events are defined
- [x] AI behaviour goes through the AI Gateway and spec 027 tools
- [x] Localization, RTL and accessibility covered
