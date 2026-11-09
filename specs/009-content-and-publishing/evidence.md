# Spec 009 evidence (R1)

The spec stays Approved, not Implemented, until its R2 and R3 parts are built: sites with their own branding and home (US6, FR-012, FR-014), campaigns (US8, FR-015), AI translation and authoring (FR-010 AI part, FR-011), mentions and hashtags (US9 R2 part), follows and recommendations (FR-017), events (FR-018), search indexing (FR-019, with spec 012) and retention (FR-020). Defaults and gaps of each R1 slice are in `docs/architecture/r1-implementation-notes.md`.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 content types (news, announcement, page) | Met for built-in types | `ContentLifecycleTests.A_draft_is_hidden_until_published_...`, `A_page_needs_an_owner_to_go_live_and_gets_a_review_date`; tenant-defined types with extra fields are R2 |
| FR-002 lifecycle with audited moves | Met | `ContentLifecycleTests.Scheduled_items_go_live_...`, `Unpublish_takes_an_item_down_...`, `ContentReviewTests.A_returned_item_goes_back_to_draft_...` |
| FR-003 approval routes on the process engine | Met for one step per site and type | `ContentReviewTests.A_submitted_item_goes_to_the_named_approver_...`, `Nobody_approves_their_own_item_...`, `Routes_are_validated_...`; multi-step needs `content.multistep_approval` (Workplace Plus) |
| FR-004 immediate, scheduled and expiry publishing, pin and feature | Met | `ContentLifecycleTests.Scheduled_items_go_live_in_the_site_time_zone_...`, `A_scheduled_next_version_keeps_the_live_one_readable_...`, `ContentReviewTests.The_feed_pages_pinned_first_...` |
| FR-005 audience targeting | Met for units with or without subtree, membership types, groups, roles and locations | `ContentReviewTests.The_feed_pages_pinned_first_and_respects_unit_targets_without_subtree_...`, `ContentLifecycleTests.Confidential_items_need_a_covering_grant_...`; profile attributes and channels are stored and refused until R2 |
| FR-006 critical announcements | Met | `ContentCriticalTests` (all), `content-governance.spec.ts` (pop-up, banner, confirm, report, en and ar, axe) |
| FR-007 immutable acknowledgement with version, channel and text hash | Met | `ContentCriticalTests.A_modal_shows_to_its_audience_until_confirmed_and_records_version_channel_and_text_hash` |
| FR-008 owners, review dates, overdue and ownerless | Met | `ContentGovernanceTests.The_owner_gets_a_review_task_...`, `An_overdue_item_shows_under_review_...`, `When_an_owner_leaves_...` |
| FR-009 versions, compare, restore | Met | `ContentGovernanceTests.History_compares_versions_and_restore_makes_a_new_draft_...`, `Text_diff_marks_words_...`, `content-governance.spec.ts` |
| FR-010 locale variants with fallback | Met for manual translation | `ContentLifecycleTests.A_reader_gets_the_tenant_default_language_with_a_fallback_note_...`; AI translation drafts are R2 |
| FR-013 content components through spec 032 manifests | Met | `ContentSocialTests.The_builder_offers_the_content_components_with_their_names`, `Without_the_content_capability_the_builder_refuses_the_components_...`, `ExperienceValidationTests.Content_components_register_with_the_content_capability_...` |
| FR-016 comments and reactions with moderation and reporting | Met for R1 (mentions and hashtags are R2) | `ContentSocialTests` (reactions, owner notice, moderation audit, reports, comments off, cross-tenant), `content-authoring.spec.ts` |
| Block editor, item pages, history and health screens (T022, T023) | Met | `content-authoring.spec.ts`, `content-governance.spec.ts` (en and ar, axe) |
| Body is a block document, no HTML, embeds from the allowlist | Met | `ContentLifecycleTests.The_body_refuses_unsafe_links_unknown_blocks_and_embeds_off_the_allowlist` |
| Tenant and org boundaries | Met | `ContentLifecycleTests.Items_and_sites_of_another_tenant_are_not_found`, `Authors_edit_only_their_own_drafts_...`, `ContentReviewTests.Routes_are_validated_and_reviews_of_another_tenant_...`, `ContentCriticalTests.Another_tenant_cannot_see_confirm_or_report_...`, `ContentGovernanceTests.Another_tenant_cannot_review_...`, `ContentSocialTests.Items_and_comments_of_another_tenant_are_not_found`, `CrossTenantSuite` |
| Entitlements | Met | `ContentLifecycleTests.The_plan_limits_the_number_of_sites`, `ContentSocialTests.Without_the_content_capability_...` |
| Feed p95 under 800 ms (50 units, 10,000 items) | Met | `ContentPerformanceTests.Feed_p95_stays_under_800_ms_...` (local run: p95 134 ms) |
| Critical check adds under 20 ms p95 | Met | `ContentPerformanceTests.Feed_p95_..._and_the_critical_check_under_20_ms_...` (local run: pending check p95 13 ms for the whole request) |
| Critical announcement to 100,000 people tracked within 2 minutes | Met | `ContentPerformanceTests.A_critical_announcement_to_100000_people_is_tracked_within_two_minutes` (local run: 3 s). Notices to the audience follow in batches of 500 |
| Scheduled publishing within 60 seconds | Met | `ContentScheduleWorker` runs every 30 seconds; `ContentLifecycleTests.Scheduled_items_go_live_in_the_site_time_zone_...` |
