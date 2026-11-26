# Implementation Plan: Workplace Booking

**Spec:** `specs/031-workplace-booking/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0013, ADR-0006, spec 016 plan (approvals, delegation), spec 022 plan (calendars), spec 018 plan (room mailboxes)

## Summary
A new `Booking` module owns locations below the site, resources, rules, bookings and check-ins. Slot conflicts are prevented with a PostgreSQL exclusion constraint on `(resource_id, tstzrange)` for confirmed and tentative bookings, so concurrency is enforced by the database. The coexistence check with 018 asks the `Integrations` contract whether a room address is an M365 room.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I tenant isolation | Yes | Forced RLS; exclusion constraint includes tenant ID |
| III authorization | Yes | Permissions and delegation relation through `IAuthorizationService` |
| V entitlements | Yes | Section 9 keys |
| Rule 5 | Yes | Calendars, approvals and room mailbox status through contracts |
| Rule 6 | Yes | `btree_gist` is a PostgreSQL contrib extension already in the image |

## Data model (schema `booking`)
`floor`, `zone`, `resource`, `resource_attribute`, `booking_rule`, `booking` (with `tstzrange`, status, series ID), `booking_series`, `check_in`

## Tests
Parallel booking race; rules on every path; recurring skips holidays; tentative hold expiry; auto release; no-show restriction; delegation; cross-tenant QR denial; reports without names; M365 room refusal.

## R3 plan (floor plans, visitors, sensors)

**Date:** 2026-11-26

### Summary
Three slices in the `Booking` module. Floor plans store one image per floor through `TenantObjectStorage` after the shared malware scanner, with each resource's position as percentages on `booking.resource`. Visitors are a `booking.visit` table owned by Booking, with emails through the 015 `IExternalEmail` contract and a reception page scoped by the new `booking.reception` permission. Sensors come from a new 017 pack `occupancy_rest` behind an `IOccupancyReadings` contract in Integrations; Booking keeps the sensor to resource mapping and the readings, and the maintenance worker reads every 5 minutes.

### Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I tenant isolation | Yes | New tables with forced RLS; object keys under the tenant prefix; visit codes resolved inside the tenant |
| III authorization | Yes | `booking.manage` for plans and sensor mapping, `booking.book` for hosts, `booking.reception` in location scope |
| V entitlements | Yes | `booking.floor_plans`, `booking.visitors`, `booking.sensors` |
| Rule 4 | Yes | Sensor reads only through the 017 connector runtime; emails through 015 |
| Rule 5 | Yes | No other module's tables are read |
| Rule 6 | Yes | No new library: images are checked by their signature bytes and size header |

### Data model
- `booking.floor`: `plan_key`, `plan_type`, `plan_width`, `plan_height`, `plan_version`.
- `booking.resource`: `plan_x`, `plan_y` (0 to 100, both or neither).
- `booking.visit`: site, host, optional booking, starts and ends, visitor name, email and company (cleared 30 days after), code, status (expected, arrived, left, cancelled), email state and attempts.
- `booking.sensor`: connection, sensor ID, resource (unique per connection and sensor).
- `booking.occupancy`: resource, reading time, occupied; hourly counts after 90 days in `booking.occupancy_hour`.
- `booking.setting.sensor_check_in` (default off).

### Tests
Floor plan upload type, size and malware refusal; positions and the availability map for a time; booking from the map refused by rules; visitor registration limits, emails, code check-in by reception of the site only, host notice, cancellation email, the 30 day purge; sensor mapping, sensor check-in inside the window only when turned on, silent connector, reports with sensed hours; package and cross-tenant denial for each.
