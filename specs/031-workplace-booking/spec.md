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
| Last updated | 2026-11-07 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | BKG-001 to BKG-018 (new) |
| Use cases | UC-08 |
| Depends on | 002 (org units, locations), 004, 005, 010 (people), 015 (reminders), 016 (approvals and services), 018 (M365 rooms, the alternative), 022 (calendars, holidays), 030 (QR check-in on mobile) |
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

**Out of scope:**
- Floor plan maps (R3), visitor management (R3), sensor occupancy (R3).
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

### Edge cases and exception flows
- Resource removed with future bookings: holders are notified and offered alternatives in the same zone.
- Site closed for a day (holiday or emergency): bookings that day are cancelled with notice.
- Booking across midnight: allowed only if the rules allow overnight (for vehicles).
- Person leaves the company: future bookings are cancelled.
- Time zone: bookings use the site's time zone and show it when different from the user's.

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

## 6. Business and validation rules
- Defaults: book up to 14 days ahead, 15 minute slots, check-in window 15 minutes before to 15 minutes after start, 3 active desk bookings per person.
- Tentative hold for approval up to 24 hours.
- No-show restriction default off.

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

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `booking.rooms` | off | on | on |
| `booking.desks` | off | on | on |
| `booking.resources` (parking, equipment) | off | on | on |
| `booking.reports` | off | on | on |

## 10. UX, localization and accessibility
- Find a space page with filters and a time grid; My bookings page and widget; resource admin pages; QR print sheet.
- The time grid is keyboard operable and announces availability to screen readers; it runs right to left in RTL.

## 11. AI behavior
None in R2.

## 12. Integrations and events
- Publishes `BookingConfirmed`, `BookingCancelled`, `BookingReleased`, `CheckedIn`.
- Consumes leaver events (019, 003), holiday calendars (022), approval outcomes (016).

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| booking.resource_created / removed | audit | resource, type, actor |
| booking.rules_changed | audit | resource or type, version, actor |
| booking.cancelled_by_admin | audit | booking, reason, actor |
| booking.activity | telemetry | type, site (no names) |

## 14. Security and abuse cases
- Hoarding: active booking limits, auto release, no-show rules.
- QR spoofing: opaque IDs, check-in only by the booking holder.
- Rule bypass through the API: server-side checks on every path.

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
