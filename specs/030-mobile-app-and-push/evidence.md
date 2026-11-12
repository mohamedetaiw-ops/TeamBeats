# Spec 030 evidence (R2)

The spec stays Approved, not Implemented. Everything in its R2 scope is built:
- the installable app per tenant and brand
- phone layouts for every R1 and R2 page
- web push as a notification channel
- offline reading of critical items
- kiosk and shared device mode
- the performance budgets

The native shell apps (FR-020, `mobile.native_shell`) are R3. The spec 030 sections of `docs/architecture/r2-implementation-notes.md` (slices 1 to 4 and the final phase) hold the defaults and deviations.

Test classes are in `tests/Integration` unless marked otherwise. `mobile.spec.ts` and `kiosk.spec.ts` are e2e specs that run in English and Arabic with axe. `budgets/mobile.spec.ts` runs on a production build in the `mobile-budgets` CI job.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 installable app with a manifest per tenant and brand | Met | `AppInstallTests.Each_organization_installs_its_own_brand_under_the_tenant_name`, `AppInstallTests.The_app_icon_is_the_tenant_initials_on_the_brand_colour`, `AppInstallTests.Signed_out_callers_and_tenants_without_the_app_get_nothing` |
| FR-002 versioned updates applied on user action, never during input | Met | `mobile.spec.ts` (a new version waits for Reload and never shows while a field has focus) |
| FR-003 every R1 and R2 page at 360 px in ar and en with no sideways scroll | Met | `mobile.spec.ts` (every page without an id, in groups of 12, both languages) |
| FR-004 touch targets of at least 44 by 44 | Met | `mobile.spec.ts` (bottom navigation targets measured) |
| FR-005 bottom navigation of at most 5 configured items with labels | Met | `ShellNavigationTests.Phones_get_up_to_five_bottom_navigation_items_in_the_chosen_order`, `mobile.spec.ts` (four entries and More, mirrored; the administrator chooses the entries) |
| FR-006 push offer after a meaningful action, decline remembered 90 days | Met | `mobile.spec.ts` (the offer waits for a meaningful action and Not now is remembered) |
| FR-007 push is a 015 channel with preferences and no Confidential content | Met | `PushTests.A_browser_subscribes_and_gets_an_encrypted_signed_message_with_no_item_content`, `PushTests.Choices_quiet_hours_and_a_device_the_service_reports_gone_are_respected`, `mobile.spec.ts` (push on and off per device and category) |
| FR-008 subscriptions deleted on sign-out, revocation and gone answers | Met | `PushTests.Sign_out_revocation_and_leaving_remove_the_devices`, `PushTests.Choices_quiet_hours_and_a_device_the_service_reports_gone_are_respected` |
| FR-009 offline cache of targeted critical items and policies, within limits, encrypted per user | Met, with a gap | `OfflineTests.Only_targeted_critical_items_and_policies_that_apply_are_bundled_and_never_confidential_ones`, `OfflineTests.The_tenant_limits_apply_and_their_change_is_audited`, `OfflineTests.The_key_belongs_to_one_session_the_package_decides_and_tenants_stay_apart`. Saved and recent items wait for saved items to exist (slice 3 notes) |
| FR-010 nothing Confidential or higher offline | Met | `OfflineTests.Only_targeted_critical_items_..._never_confidential_ones` |
| FR-011 offline cache wiped on sign-out and expiry, pruned when access ends | Met | `mobile.spec.ts` (a kept announcement opens offline, and sign-out deletes it); the cache is replaced at each sync |
| FR-012 offline banner with the last update time | Met | `mobile.spec.ts` (opens under the offline banner) |
| FR-013 mobile sessions follow the 026 device policy | Met | Phones use the same browser session and the 026 session policy (lifetime, idle timeout, revocation); kiosk sessions add their own idle time (`KioskTests.A_kiosk_session_ends_after_the_kiosk_idle_time_and_at_once_when_the_kiosk_is_revoked`) |
| FR-014 kiosks enrolled by a device administrator, revocable within 1 minute | Met | `KioskTests.A_device_administrator_enrols_a_kiosk_and_its_people_sign_in_with_badge_and_PIN`, `KioskTests.A_kiosk_session_ends_after_the_kiosk_idle_time_and_at_once_when_the_kiosk_is_revoked` (sessions end at once, the start screen checks every 30 seconds), `kiosk.spec.ts` |
| FR-015 badge code or QR plus PIN, lockout after 5 failures | Met | `KioskTests.A_device_administrator_enrols_...`, `KioskTests.Five_wrong_PINs_lock_the_badge_on_every_kiosk_and_tell_its_owner`, `KioskRulesTests`, `kiosk.spec.ts` (first PIN chosen at the kiosk) |
| FR-016 kiosk sessions end after inactivity and leave nothing on the device | Met | `KioskTests.A_kiosk_session_ends_after_the_kiosk_idle_time_...`, `KioskRulesTests.A_kiosk_session_ends_when_the_device_is_revoked_used_elsewhere_or_left_idle`, `kiosk.spec.ts` (idle warning, sign-out, storage and caches empty) |
| FR-017 features not for shared devices blocked on the server | Met | `KioskRulesTests.Kiosk_sessions_reach_only_listed_routes_of_allowed_features`, `KioskTests.Every_route_a_kiosk_may_reach_exists`, `kiosk.spec.ts` (payslips refused by the API with `authz.denied.shared_device`) |
| FR-018 deep links open the target page in the installed app | Met | The manifest scope is the whole tenant host, so links inside it open in the installed app; a tapped push focuses or opens the app on its link (`public/sw.js`, slice 2 notes) |
| FR-019 the service worker caches no API answer across people or tenants | Met | `public/sw.js` never handles `/api` or `/dev` and never stores a page; only the build files, icons and the signed-out offline page are kept (slice 1 notes) |
| FR-020 native shell apps | R3 | Not in R2 |
| Section 9 package entitlements | Met | `AppInstallTests.Signed_out_callers_and_tenants_without_the_app_get_nothing`, `PushTests.Tenants_never_reach_each_other_s_devices_and_the_package_decides`, `OfflineTests.The_key_belongs_to_one_session_the_package_decides_...`, `KioskTests.Other_tenants_badges_and_devices_are_strangers_and_the_package_decides` |
| Section 12 events | Met | `DeviceEnrolled` and `DeviceRevoked` (`KioskTests`), `PushSubscriptionCreated` and `PushSubscriptionRemoved` (`PushTests`) |
| Section 13 audit and telemetry | Met | `device.kiosk_enrolled`, `revoked`, `signin` and `lockout` (`KioskTests`), `mobile.offline_policy_changed` (`OfflineTests.The_tenant_limits_apply_and_their_change_is_audited`), `mobile.install` (`AppInstallTests.An_install_is_counted_by_platform_family_without_the_person`, sent by the app on the browser's install event) and `mobile.push_optin` (`PushTests`), each with the platform family only |
| Section 15 home under 3 seconds on a mid-range phone over 4G, cached shell under 1 second | Met, measured in the browser | `budgets/mobile.spec.ts`, CI job `mobile-budgets`: the Lighthouse mobile profile (slow 4G, a CPU four times slower, 412 by 823 screen) on a production build. The home page's Largest Contentful Paint was about 0.85 s and the cached first paint about 0.25 s locally, in both languages. The lighthouse package is not used, because it brings a dependency under a license that is not OSI approved (final phase notes) |
| Section 15 Lighthouse PWA installability | Met, without Lighthouse | Lighthouse no longer has an installability category. The manifest fields and icons (`AppInstallTests`) and the service worker install and offline answer (`mobile.spec.ts`) cover what it checked |
| SC-001 half of frontline employees use the app weekly in pilots | Not proven | Needs pilot tenants; `mobile.install` and push opt-in telemetry measure it |
| SC-002 zero personal data on kiosk devices after sign-out | Met | `kiosk.spec.ts` (local and session storage and caches other than the app shell are empty after the idle sign-out) |
| Open question: badge format | Default kept | A QR code printed from TeamBeats plus the employee number (slice 4 notes) |
| Cross-tenant, org boundary and package denial | Met | Every class above has a case for another tenant and the package; kiosk unit scope in `KioskTests.A_device_administrator_enrols_...` |
