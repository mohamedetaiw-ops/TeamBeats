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
