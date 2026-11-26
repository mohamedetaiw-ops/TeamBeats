# Feature Specification: Workplace Booking

| Field | Value |
|---|---|
| Spec ID | 031-workplace-booking |
| Area | workflows |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R2 (bookable rooms, desks, parking and equipment for tenants without Microsoft 365 room mailboxes, sites, floors and zones, availability calendar, booking rules, recurring bookings, approval for restricted resources, check-in with auto release, booking on behalf, usage reports), R3 (floor plan maps, visitor booking, sensor based occupancy through connectors) |
| Last updated | 2026-11-26 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | BKG-001 to BKG-018 (R2), BKG-019 to BKG-027 (R3) |
| Use cases | UC-08 |
| Depends on | 002 (org units, locations), 004, 005, 010 (people), 011 (malware scan of uploads), 015 (reminders, external email), 016 (approvals and services), 017 (occupancy connector, R3), 018 (M365 rooms, the alternative), 022 (calendars, holidays), 030 (QR check-in on mobile) |
| Used by | 008 (My bookings widget), 016 (booking as a service), 021 (usage counts) |

## 1. Problem and outcome

**Problem:** Tenants that do not use Microsoft 365 room mailboxes (or that need desks, parking and equipment, which room mailboxes do not cover well) book on paper or in spreadsheets. With hybrid work, desks sit empty while people cannot find one, and booked rooms stay empty because nobody turned up.

**Outcome:** A `Booking` module lets facilities administrators define sites, floors, zones and resources with rules. Employees find and book what they need, check in from their phone, and unused bookings release automatically. Tenants that use Microsoft 365 rooms keep them through 018; this module can still handle desks, parking and equipment for them. Booking never runs for the same room in both systems.

## 2. Scope

**In scope:**
- Locations: sites (from 002 locations), floors and zones.
- Resource types: room (capacity, facilities), desk (fixed or hot desk), parking space, equipment (laptops, projectors, vehicles) with tenant-defined attributes.
- Availability calendar per resource and search by time, site, capacity and attributes.
- Booking rules per resource or type: who can book (org units, groups), how far ahead, minimum and maximum length, working hours from the site calendar (022), buffer between bookings, maximum active bookings per person.
- Recurring bookings (daily, weekly) with exceptions.
- Approval for restricted resources through the 016 approval route.
- Check-in: by QR code on the resource (030), from the booking, or at a kiosk; auto release after a grace period (default 15 minutes) if not checked in.
- Booking on behalf for assistants, and team booking of neighbouring desks.
- Reminders and changes through 015; calendar file (.ics) for each booking.
- Reports: utilization by resource, zone and site, no-show rates.
- Coexistence with 018: a room is either an M365 room mailbox or a TeamBeats room, never both.

**In scope for R3:**
- Floor plan maps: an image per floor with resources placed on it, showing availability for the chosen time, with a list that offers the same choices.
- Visitor booking: a host registers a visitor for a site and time; the visitor gets an email with a visit code; reception checks them in and the host is told.
- Sensor based occupancy: occupancy readings from sensors through a 017 connector, mapped to resources, used to check bookings in and shown in reports as counts.

**Out of scope:**
- Visitor badges printing, NDAs and watch lists (a later visitor management spec).
- Indoor positioning or tracking of people; sensors report a resource as occupied or free, never who is there.
- Catering and service orders for meetings (use 016 services).
- Payment or chargeback.

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Search, book, check in, cancel own bookings within rules | `booking.book` |
| Assistant | Book on behalf of people who delegated to them | Delegation relation (016) |
| Facilities administrator | Define locations, resources, rules, QR codes; cancel any booking with a reason | `booking.manage` in location scope |
| Resource approver | Approve or reject restricted bookings | Approver in the resource's approval route |
| Facilities analyst | See utilization reports | `booking.report` in location scope |
| Host (R3) | Register visitors for a site, cancel their own visits | `booking.book` |
| Reception (R3) | See the day's visitors of a site, check them in and out | `booking.reception` in location scope |

## 4. User scenarios

### User Story 1: Set up a site (Priority: P1, R2)

Hana, facilities administrator, adds the Riyadh office with 2 floors, 8 rooms, 120 hot desks and 40 parking spaces.

**Why this priority:** nothing to book without resources.

**Independent test:** create a site, floor, zone and resources of each type with rules; book as an employee inside and outside the rules.

**Acceptance scenarios:**
1. **Given** the Riyadh location from 002, **When** Hana adds floors, zones and resources, **Then** each resource gets a QR code she can print.
2. **Given** a room already linked as an M365 room mailbox in 018, **When** Hana tries to add it as a TeamBeats room, **Then** it is refused with the reason.
3. **Given** rules "Finance only, up to 14 days ahead, 30 minutes to 4 hours", **Then** they apply on the server to every booking path.

### User Story 2: Find and book (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** search for a room for 6 people tomorrow 10:00 to 11:00 with a screen, **Then** Laila sees available rooms that match and that she may book, with capacity and facilities.
2. **Given** she books, **Then** the booking is confirmed at once, she gets a confirmation with an .ics file, and the slot is held against parallel attempts (only one of two simultaneous bookings succeeds).
3. **Given** a weekly recurring booking with one holiday in the series (022), **Then** the holiday occurrence is skipped and listed.
4. **Given** she already has the maximum active bookings, **Then** the booking is refused with the limit shown.

### User Story 3: Book a restricted resource (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** the board room needs approval, **When** Omar requests it, **Then** the slot is held as tentative, the approval goes to the 016 inbox, and the hold ends if not approved within 24 hours or before the start.
2. **Given** approval, **Then** the booking is confirmed and Omar is notified; on rejection the slot frees and Omar sees the reason.

### User Story 4: Check in or lose it (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** a desk booking from 09:00, **When** Laila scans the desk QR between 08:45 and 09:15, **Then** she is checked in.
2. **Given** no check-in by 09:15, **Then** the booking is released, the desk is free for others, and Laila is told.
3. **Given** three no-shows in 30 days, **Then** Laila is reminded of the rule; the tenant may restrict her advance booking window for 14 days.
4. **Given** a resource with check-in off (for example equipment handed over by a desk), **Then** no auto release happens.

### User Story 5: Book for others and as a team (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** Mona is the delegate of a director (016 delegation), **Then** she can book on the director's behalf and the director is notified.
2. **Given** a team of 6, **When** Ahmed books 6 desks in one zone for Tuesday, **Then** each person gets their own booking they can cancel, subject to their own limits.

### User Story 6: See how space is used (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** bookings and check-ins, **Then** the analyst sees utilization (checked-in hours over available hours) and no-show rates by resource, zone, site and week.
2. **Given** reports, **Then** they show no names, only counts, unless the viewer is a facilities administrator looking at a single booking.

### User Story 7: Pick a desk on the floor plan (Priority: P1, R3)

Hana uploads the plan of the second floor and places the desks and rooms on it. Laila picks a desk next to her team from the map.

**Acceptance scenarios:**
1. **Given** a floor, **When** Hana uploads a PNG, JPEG or WebP plan of up to 5 MB, **Then** it is scanned for malware and shown on the floor, and she places each resource of the floor on it by position.
2. **Given** a time, **When** Laila opens the floor, **Then** each placed resource shows as free, booked or not for her (rules), and choosing a free one books it through the same rules as search.
3. **Given** a keyboard or screen reader user, **Then** the same resources and actions are offered as a list next to the map, in the same order, and the map works right to left.
4. **Given** a resource removed or moved to another floor, **Then** its place on the old plan goes with it.

### User Story 8: Expect a visitor (Priority: P2, R3)

Omar expects a supplier at the Riyadh office on Tuesday at 10:00.

**Acceptance scenarios:**
1. **Given** a site, **When** Omar registers a visitor with name, email and optional company for a day and time, optionally with one of his bookings, **Then** the visitor gets an email with the site, the time, the host's name and a visit code.
2. **Given** the visit day, **When** reception opens the site's visitors, **Then** they see the day's visitors with host and time, and check one in by code or from the list; the host is told the visitor has arrived.
3. **Given** a visit that is cancelled or changed by the host, **Then** the visitor gets an email saying so.
4. **Given** 30 days after the visit, **Then** the visitor's name, email and company are deleted and only counts remain.

### User Story 9: Know a desk is really used (Priority: P3, R3)

The tenant has desk and room sensors behind its building system.

**Acceptance scenarios:**
1. **Given** an administrator who made an occupancy connection in 017 and mapped sensor IDs to resources, **When** a sensor reports occupied during a booking's check-in window, **Then** the booking is checked in by the sensor when the tenant turned sensor check-in on.
2. **Given** readings, **Then** the utilization report shows sensed occupied hours next to checked-in hours, as counts without names.
3. **Given** the connector is unavailable or a sensor is silent, **Then** bookings follow the R2 check-in and release rules and nothing is checked in by the sensor.

### Edge cases and exception flows
- Resource removed with future bookings: holders are notified and offered alternatives in the same zone.
- Site closed for a day (holiday or emergency): bookings that day are cancelled with notice.
- Booking across midnight: allowed only if the rules allow overnight (for vehicles).
- Person leaves the company: future bookings are cancelled.
- Time zone: bookings use the site's time zone and show it when different from the user's.
- R3: a floor plan replaced by a new image keeps the positions, which are stored relative to the image; resources placed outside the new image are listed as not placed.
- R3: a visitor whose host leaves the company: future visits are cancelled and the visitor is told.
- R3: two readings for the same sensor at the same time: the later one counts; readings older than one hour are ignored.

## 5. Functional requirements

- **BKG-001 / FR-001**: Facilities administrators MUST define sites from 002 locations, floors, zones and resources of the types in section 2.
- **BKG-002 / FR-002**: A room MUST NOT be both an 018 room mailbox and a TeamBeats room.
- **BKG-003 / FR-003**: Booking rules MUST be enforced on the server for every path (web, mobile, kiosk, on behalf, API).
- **BKG-004 / FR-004**: Search MUST return only available resources the user may book, filtered by time, site, capacity and attributes.
- **BKG-005 / FR-005**: Concurrent bookings of the same slot MUST result in exactly one success.
- **BKG-006 / FR-006**: Recurring bookings MUST skip non-working days of the site calendar (022) and list skipped occurrences.
- **BKG-007 / FR-007**: Restricted resources MUST use a 016 approval route with a tentative hold that expires.
- **BKG-008 / FR-008**: Check-in MUST be possible by QR, from the booking, or at a kiosk within the check-in window.
- **BKG-009 / FR-009**: Bookings without check-in MUST be released after the grace period when check-in is on.
- **BKG-010 / FR-010**: No-show rules MUST be tenant configuration.
- **BKG-011 / FR-011**: Booking on behalf MUST require a delegation relation and notify the person booked for.
- **BKG-012 / FR-012**: Team bookings MUST create individual bookings subject to each person's limits.
- **BKG-013 / FR-013**: Confirmations, reminders, releases and cancellations MUST notify through 015 and include an .ics file.
- **BKG-014 / FR-014**: Utilization reports MUST show counts without names.
- **BKG-015 / FR-015**: Removing a resource or closing a site MUST notify holders of affected bookings.
- **BKG-016 / FR-016**: Leavers' future bookings MUST be cancelled.
- **BKG-017 / FR-017**: Bookings MUST use the site's time zone and show it when it differs from the user's.
- **BKG-018 / FR-018**: All user-facing text MUST use resource keys and work in RTL.
- **BKG-019 / FR-019**: Facilities administrators MUST be able to upload a floor plan image per floor, scanned for malware, and place the floor's resources on it.
- **BKG-020 / FR-020**: The floor plan MUST show each placed resource's availability for the chosen time and book through the same server rules as search.
- **BKG-021 / FR-021**: The floor plan MUST have a list alternative with the same resources and actions, and work by keyboard, with screen readers and in RTL.
- **BKG-022 / FR-022**: Employees MUST be able to register visitors for a site and time, optionally with their booking, and cancel or change their own visits.
- **BKG-023 / FR-023**: Visitors MUST get an email with an opaque visit code, and an email when the visit is cancelled or changed.
- **BKG-024 / FR-024**: Reception MUST see the day's visitors of the sites in their scope and check them in by code or from the list; the host MUST be told on arrival.
- **BKG-025 / FR-025**: Visitor personal data MUST be deleted 30 days after the visit; reports show counts only.
- **BKG-026 / FR-026**: Occupancy readings MUST come through a 017 connector, mapped from sensor IDs to resources, and carry no personal data.
- **BKG-027 / FR-027**: When the tenant turns sensor check-in on, a reading of occupied in the check-in window MUST check the booking in; utilization reports MUST show sensed occupied hours as counts.

## 6. Business and validation rules
- Defaults: book up to 14 days ahead, 15 minute slots, check-in window 15 minutes before to 15 minutes after start, 3 active desk bookings per person.
- Tentative hold for approval up to 24 hours.
- No-show restriction default off.
- R3: a floor plan image is PNG, JPEG or WebP, up to 5 MB and up to 8,000 pixels a side; positions are percentages of its width and height.
- R3: up to 20 visitors per host per day; a visit is between 15 minutes and 12 hours on one day, up to 90 days ahead; visit codes are 8 characters, unique per tenant, and work only on the visit day.
- R3: occupancy is read every 5 minutes; sensor check-in default off; readings are kept 90 days, then hourly counts only.

## 7. Tenant and organization scope
- Tenant-owned data: locations, resources, rules, bookings, check-ins.
- Organization scoping: rules restrict by org unit and group; administrators act within location scope.
- Cross-tenant denial cases: QR codes carry an opaque ID resolved only within the scanning user's tenant.
- Sibling, parent and child cases: a Finance-only room refuses HR employees; a parent unit rule includes child units.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Locations, resources, rules | Product | Internal | Life of resource | No |
| Bookings and check-ins | Product | Internal | 1 year, then counts only | Yes |
| Floor plans and positions (R3) | Product | Internal | Life of floor | No |
| Visits (R3) | Product | Confidential | 30 days after the visit, then counts only | Yes (visitor name, email, company) |
| Occupancy readings (R3) | Connector | Internal | 90 days, then hourly counts | No |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `booking.rooms` | off | on | on |
| `booking.desks` | off | on | on |
| `booking.resources` (parking, equipment) | off | on | on |
| `booking.reports` | off | on | on |
| `booking.floor_plans` (R3) | off | on | on |
| `booking.visitors` (R3) | off | on | on |
| `booking.sensors` (R3) | off | off | on |

## 10. UX, localization and accessibility
- Find a space page with filters and a time grid; My bookings page and widget; resource admin pages; QR print sheet.
- The time grid is keyboard operable and announces availability to screen readers; it runs right to left in RTL.
- R3: floor plan page with a time picker, the map and its list; floor plan editor for administrators that places resources by pointer or by entering a position; Visitors page for hosts; Reception page per site.

## 11. AI behavior
None in R2 or R3.

## 12. Integrations and events
- Publishes `BookingConfirmed`, `BookingCancelled`, `BookingReleased`, `CheckedIn`.
- Consumes leaver events (019, 003), holiday calendars (022), approval outcomes (016).
- R3: an occupancy connector pack in 017 (`occupancy_rest`, a REST endpoint that lists sensors and their state). Visitor emails go through the 015 external email contract. Floor plan images are scanned through the shared malware scanner before they are stored.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| booking.resource_created / removed | audit | resource, type, actor |
| booking.rules_changed | audit | resource or type, version, actor |
| booking.cancelled_by_admin | audit | booking, reason, actor |
| booking.floor_plan_changed (R3) | audit | floor, actor |
| booking.visit_registered / cancelled (R3) | audit | visit, site, actor (no visitor details) |
| booking.visitor_checked_in (R3) | audit | visit, site, actor |
| booking.sensors_mapped (R3) | audit | connection, resources mapped, actor |
| booking.activity | telemetry | type, site (no names) |

## 14. Security and abuse cases
- Hoarding: active booking limits, auto release, no-show rules.
- QR spoofing: opaque IDs, check-in only by the booking holder.
- Rule bypass through the API: server-side checks on every path.
- R3: floor plan uploads are images only, checked by content and scanned; SVG is refused because it can carry script.
- R3: visit codes are random, used only on the visit day and only by reception of that site; guessing is limited by the request rate limit.
- R3: sensor readings name resources, never people; a reading for an unmapped sensor is ignored.

## 15. Non-functional requirements
- Search under 700 ms p95 for a site with 1,000 resources.
- Release job runs every minute.

## 16. Success criteria
- **SC-001**: No-show rate under 10% after 2 months in pilot sites with check-in on.
- **SC-002**: No double bookings in load tests with 100 parallel attempts.

## 17. Open questions
- [NEEDS CLARIFICATION: should desk bookings be anonymous to colleagues?] Default: colleagues see "booked" without names unless the tenant enables "see who sits where".

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| BKG-001 to BKG-003 | US1 |
| BKG-004 to BKG-006 | US2 |
| BKG-007 | US3 |
| BKG-008 to BKG-010 | US4 |
| BKG-011, BKG-012 | US5 |
| BKG-014 | US6 |
| BKG-019 to BKG-021 | US7 |
| BKG-022 to BKG-025 | US8 |
| BKG-026, BKG-027 | US9 |
