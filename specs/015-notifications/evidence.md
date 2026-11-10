# Spec 015 evidence (R1)

The spec stays Approved, not Implemented. R1 builds the send contract, the in-app centre, email over SMTP, preferences with mandatory categories exempt, grouping, the daily digest, quiet hours, templates per locale, delivery records, retries and bounces. Sending modules use it: critical announcements, content reviews and comments (009), documents (011), request updates and approvals (016), policy approvals and attestations (025), security alerts (026), agent owners and lifecycle (020, 027) and evaluation gates (034). The R2 and R3 parts are not built: Teams delivery and quick actions (FR-017, FR-018), push, Slack and SMS (FR-019, FR-020), signage (FR-021) and audience delivery reports (FR-022). The spec 015 section of `docs/architecture/r1-implementation-notes.md` holds the defaults, deviations and gaps.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 only the notification contract sends | Met | Every sending module calls `INotificationSender`; SMTP lives only in the Notifications module |
| FR-002 category, urgency, mandatory flag, template and link | Met | `NotificationsTests.A_notification_reaches_the_centre_with_its_text_and_the_count_and_read_state_follow`, `Unknown_categories_and_long_parameters_are_refused`, `Links_must_stay_on_the_tenant_host` |
| FR-003 centre with an unread count | Met | `NotificationsTests.A_notification_reaches_the_centre_with_its_text_and_the_count_and_read_state_follow`, `The_centre_pages_with_a_cursor_and_filters_by_mandatory`, `Read_all_clears_the_count`, `notifications.spec.ts`. The count is polled every 30 seconds (see the notes) |
| FR-004 email over SMTP, platform or tenant relay | Met | `NotificationsTests.Email_goes_out_in_the_recipients_language_with_an_absolute_link_and_the_tenant_sender_name`, `A_tenant_relay_needs_the_capability_in_the_plan`, `A_tenant_relay_is_used_for_delivery_and_the_test_email_is_audited_without_secrets` |
| FR-005 channel choice per category, mandatory exempt | Met | `NotificationsTests.Categories_with_email_off_by_default_send_no_email_until_the_user_turns_it_on`, `Turning_off_email_for_a_mandatory_category_has_no_effect`, `A_tenant_can_make_another_category_mandatory`, `Digest_categories_are_combined_into_one_email_at_the_digest_time`, `NotificationRulesTests.Platform_mandatory_categories_are_fixed` |
| FR-006 templates per locale | Met | `NotificationsTests.Text_is_rendered_in_the_readers_language`, `Email_goes_out_in_the_recipients_language_with_an_absolute_link_and_the_tenant_sender_name` |
| FR-007 only what the recipient may see, no secrets | Met | `NotificationRulesTests.Template_parameters_are_filled_as_plain_text_and_missing_ones_become_empty`, `NotificationsTests.Unknown_categories_and_long_parameters_are_refused`; senders pass identifiers and titles the recipient may read |
| FR-008 retries for 24 hours with outcomes | Met | `NotificationsTests.A_failing_relay_is_retried_with_backoff_and_marked_failed_after_24_hours`, `NotificationRulesTests.Backoff_grows_and_then_stays_at_three_hours` |
| FR-009 dedupe within 1 hour | Met | `NotificationsTests.The_same_dedupe_key_within_an_hour_is_sent_once` |
| FR-010 hard bounces pause email | Met for synchronous refusals | `NotificationsTests.A_hard_bounce_pauses_email_to_the_address_and_the_user_sees_it` |
| FR-011 disabled members receive nothing | Met | `NotificationsTests.A_disabled_member_receives_nothing`, `A_member_disabled_before_delivery_gets_no_email` |
| FR-012 kept 90 days | Met | `NotificationsTests.Old_notifications_are_purged_after_90_days` |
| FR-013 grouping | Met | `NotificationsTests.Related_notifications_are_grouped_into_one_entry_with_a_count` |
| FR-014 quiet hours | Met | `NotificationsTests.Quiet_hours_hold_email_until_they_end_but_urgent_mandatory_messages_go_at_once`, `NotificationRulesTests` quiet hours cases |
| FR-015 opening re-checks access | Met by design | A notification holds only a path; the target page checks the person's access when it opens |
| FR-016 template overrides keep parameters | Met | `NotificationsTests.A_template_override_must_keep_the_platform_parameters_and_changes_the_text`, `A_template_override_in_one_tenant_does_not_change_another` |
| FR-017 to FR-022 other channels, signage, reports | R2 | Not built |
| FR-023 recipients in the sender's tenant only | Met | `NotificationsTests.Another_tenants_user_is_skipped_and_another_tenant_cannot_see_the_row`, `Each_user_sees_only_their_own_notifications_and_cannot_read_anothers` |
| FR-024 keyboard and screen reader | Met | `notifications.spec.ts` with axe in English and Arabic |
| Burst of 100,000 within 2 minutes | Met | `NotificationBurstTests.A_burst_of_100000_notifications_creates_every_in_app_record_within_two_minutes` (about 10 seconds locally, with 100,000 email deliveries queued) |
| SC-001 every R1 notify through the contract | Met | As FR-001 |
| SC-002 fewer than 5% turn off email | Pilot | Measured in pilot tenants |
