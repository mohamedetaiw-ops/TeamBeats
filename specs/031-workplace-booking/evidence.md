# Spec 031 evidence (R2)

The spec stays Approved, not Implemented. Built so far: sites, floors, zones and resources with QR labels, rules on the server for every path, search, the parallel booking race, recurring series that skip non-working days, approval holds on the task inbox, check-in by QR, from the booking and at a kiosk, release of no-shows and the no-show rule, booking on behalf and for a team, cancellations with calendar files, leavers, and usage reports. Still to come in R2, recorded in the spec 031 sections of `docs/architecture/r2-implementation-notes.md`: closing a site for a day when a holiday is added or in an emergency, the availability time grid on Find a space, the one year retention of bookings and the `booking.activity` telemetry. Test classes are in `tests/Integration` unless marked e2e. The e2e suite (`booking.spec.ts`) runs in English and Arabic with axe.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 sites, floors, zones and resources of the four kinds | Met | `BookingTests.A_facilities_administrator_sets_up_a_site_with_resources_and_printable_codes_within_their_scope`, `booking.spec.ts` (adds a site, a floor and a desk) |
| FR-002 a room is not both a Microsoft 365 room mailbox and a TeamBeats room | Met | `BookingTests.A_room_that_is_a_Microsoft_365_room_mailbox_is_refused` |
| FR-003 rules enforced on the server for every path | Met | `BookingTests.Rules_apply_on_the_server_for_units_how_far_ahead_length_and_opening_hours`, `BookingForOthersTests` (on behalf and team paths), `BookingFlowTests.People_check_in_by_QR_from_the_booking_or_at_a_kiosk_only_within_the_window` (scan path) |
| FR-004 search returns only free resources the person may book | Met | `BookingTests.Search_shows_only_free_resources_that_match_and_that_the_person_may_book`, `booking.spec.ts` |
| FR-005 parallel bookings of one slot give exactly one success | Met | `BookingTests.Of_many_parallel_attempts_on_one_slot_exactly_one_succeeds` (exclusion constraint) |
| FR-006 series skip non-working days and list them | Met | `BookingTests.A_weekly_series_skips_a_holiday_and_counts_once_against_the_limit_of_active_bookings` |
| FR-007 restricted resources on an approval route with an expiring hold | Met | `BookingFlowTests.A_restricted_room_is_held_until_an_approver_in_the_inbox_approves_or_rejects_it_with_a_reason`, `BookingFlowTests.A_hold_nobody_decides_in_time_expires_and_frees_the_slot` |
| FR-008 check-in by QR, from the booking or at a kiosk within the window | Met | `BookingFlowTests.People_check_in_by_QR_from_the_booking_or_at_a_kiosk_only_within_the_window`, `booking.spec.ts` (scan and check in) |
| FR-009 bookings without check-in released after the grace period | Met | `BookingFlowTests.Bookings_nobody_checks_in_to_are_released_and_the_no_show_rule_restricts_how_far_ahead` |
| FR-010 no-show rules are tenant configuration | Met | Same test, with the restriction on and off through `PUT /manage/settings` |
| FR-011 booking on behalf needs a delegation and tells the person | Met | `BookingForOthersTests.A_delegate_books_on_behalf_under_the_holders_rules_and_the_holder_is_told` |
| FR-012 team bookings are individual and follow each person's limits | Met | `BookingForOthersTests.A_manager_books_one_desk_each_for_the_team_under_each_persons_own_limits` |
| FR-013 notifications through 015 with an .ics file | Met | `BookingTests.People_cancel_their_own_bookings_and_administrators_cancel_with_a_reason_and_get_a_calendar_file` (notifications link to the booking, which offers the file) |
| FR-014 utilization reports show counts without names | Met | `BookingReportTests.An_analyst_sees_utilization_and_no_show_rates_by_resource_zone_site_and_week_without_names_of_people`, `booking.spec.ts` (usage report) |
| FR-015 removing a resource or closing a site tells the holders | Met | `BookingTests.Removing_a_resource_or_a_person_leaving_cancels_their_future_bookings` |
| FR-016 leavers' future bookings cancelled | Met | Same test |
| FR-017 the site's time zone, shown when it differs | Met | `BookingTests.Rules_apply_on_the_server_...` (site zone), the booking views carry the site zone and the pages show it when it differs |
| FR-018 resource keys and RTL | Met | The catalogue and message checks in `npm run verify`; `booking.spec.ts` in Arabic |
| Edge case: a site closed for a day cancels that day's bookings with notice | Not yet | Follows in the next slice |
| Section 8 retention: bookings kept one year, then counts only | Not yet | Follows in the next slice |
| Section 10 time grid on Find a space | Not yet | The availability endpoint exists (`GET /resources/{id}/availability`); the grid follows in the next slice |
| Package entitlements (section 9) | Met | `BookingTests.Workplace_has_no_booking`, `BookingFlowTests.Workplace_has_no_check_in`, `BookingReportTests.People_without_the_report_right_in_the_site_scope_other_tenants_and_smaller_packages_get_no_report` |
| Audit events (section 13) | Met | Site, resource, rule, settings and administrator cancellation changes are written to the audit log (`BookingTests` reads them); `booking.activity` telemetry follows in the next slice |
| Search under 700 ms p95 at 1,000 resources | Met | `BookingPerformanceTests.Search_and_the_usage_report_stay_under_700_ms_p95_at_1000_resources` (about 30 ms locally) |
| Release job every minute | Met | `BookingMaintenanceWorker` ticks every minute per tenant |
| SC-001 no-show rate under 10% after two months in pilots | Not proven | Needs pilot tenants; the usage report measures it |
| SC-002 no double bookings with 100 parallel attempts | Met | `BookingTests.Of_many_parallel_attempts_on_one_slot_exactly_one_succeeds` |
| Open question: are desk bookings anonymous to colleagues | Default kept | Colleagues see "booked" without a name unless the tenant turns on showing names |
| Cross-tenant, org boundary and package denial | Met | Every class above has a case for another tenant and the package; QR codes resolve only in the scanning person's tenant |
