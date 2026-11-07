# Feature Specification: Mobile App, PWA and Push

| Field | Value |
|---|---|
| Spec ID | 030-mobile-app-and-push |
| Area | experience |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R2 (installable progressive web app with tenant branding, mobile layouts for every R1 and R2 page, web push notifications, offline reading of critical items, mobile sign-in with session rules, kiosk and shared device mode for frontline staff), R3 (store-published native shell apps for iOS and Android wrapping the PWA, native push, biometric unlock, managed app configuration for MDM) |
| Last updated | 2026-11-07 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | EXP-009, MOB-001 to MOB-020 (new) |
| Use cases | UC-01, UC-03 |
| Depends on | 003 (sessions, sign-in), 007 (localization, accessibility), 008 (shell, home), 009 (critical content), 015 (push channel), 025 (critical policies), 026 (device and session policy), 033 (branding) |
| Used by | Every user-facing spec, which must work at mobile widths |

## 1. Problem and outcome

**Problem:** Frontline staff (drivers, store staff, factory workers) have no desktop and often no company email; office staff read the intranet on their phones between meetings. A separate native app per customer is expensive to build, publish and keep in step with the web.

**Outcome:** TeamBeats is an installable PWA with each tenant's name, icon and colours (033). Every page works on a phone. Employees opt in to push notifications, read critical items offline, and frontline teams share a kiosk device without seeing each other's data. Native store apps in R3 wrap the same PWA, so features never fork. This settles the foundation open decision: PWA first, native shell second.

## 2. Scope

**In scope:**
- Web app manifest per tenant (name, short name, icons, theme colour from 033), served from the tenant's host.
- Service worker: app shell caching, versioned updates with a "new version" prompt, offline page.
- Mobile layouts: bottom navigation with up to 5 items configured in 008, touch targets of at least 44 by 44 points, every R1 and R2 page usable at 360 px width.
- Web push through the 015 push channel: opt-in prompt after a meaningful action (never on first visit), per-device subscription, notification preferences shared with 015.
- Offline reading: critical announcements (009), mandatory policies (025), the user's saved items and recent pages, cached encrypted per user with a tenant-set limit and expiry.
- Mobile sign-in: same identity flows (003); session lifetime and idle timeout set by 026 device policy for mobile.
- Kiosk and shared device mode: a device enrolled by an administrator signs users in by badge code or QR plus PIN, signs them out after inactivity, keeps no personal data between users, and hides Confidential features.
- Deep links from notifications and emails open the right page in the installed app.

**Out of scope:**
- Store-published native apps, native push and biometrics (R3).
- Offline writing (forms, posts) except queueing a leave request draft (R3).
- MDM app configuration (R3).

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Install the app, enable push, read offline items | Signed in |
| Frontline employee | Sign in on a kiosk device with badge code or QR and PIN | `kiosk.signin` (granted by group) |
| Device administrator | Enrol kiosk devices, set kiosk features and timeouts, revoke devices | `device.manage` |
| Security administrator | Set mobile session lifetime, idle timeout, offline storage limits | `security.manage` (026) |
| Experience administrator | Set bottom navigation items and offline critical categories | `experience.manage` (008) |

## 4. User scenarios

### User Story 1: Install the intranet on my phone (Priority: P1, R2)

**Why this priority:** first contact for mobile employees.

**Independent test:** open the tenant host on a phone, install, check the name, icon and colours are the tenant's, check every page at 360 px.

**Acceptance scenarios:**
1. **Given** Acme's branding, **When** Laila installs the app, **Then** the home screen shows Acme's icon and short name, and the splash uses Acme's colours.
2. **Given** a new version is deployed, **When** Laila opens the app, **Then** she sees "a new version is ready" and the app reloads on her tap, never in the middle of a form.
3. **Given** a tenant with two brands (033 multi-organization), **Then** the manifest follows the brand of the user's organization.

### User Story 2: Get push notifications (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** Laila completed her first leave request, **Then** she is asked once whether to allow notifications on this device; declining is remembered for 90 days.
2. **Given** push is enabled, **When** a notification in a category she chose for push arrives (015), **Then** the device shows it with a title and a short text that contains no Confidential content, and tapping opens the item.
3. **Given** she signs out or the device is revoked, **Then** the push subscription is deleted and nothing more is sent.
4. **Given** a subscription the push service reports gone, **Then** it is removed at once.

### User Story 3: Read critical items without a connection (Priority: P1, R2)

Omar drives between sites with weak coverage.

**Acceptance scenarios:**
1. **Given** critical announcements and mandatory policies targeted to Omar, **When** the app is online, **Then** it caches them, up to the tenant limit (default 50 items, 50 MB), encrypted with a key held in the session.
2. **Given** no connection, **Then** Omar can open cached items with a banner "offline, last updated at" and nothing else pretends to work.
3. **Given** an item is withdrawn or his access ends, **Then** it is removed from the cache at the next sync; after sign-out or session expiry the cache is wiped.
4. **Given** a Confidential item, **Then** it is never cached offline.

### User Story 4: Share a kiosk device on the shop floor (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** a tablet enrolled as a kiosk for the Riyadh warehouse, **When** Khaled scans his badge QR and enters his PIN, **Then** he is signed in with kiosk features only (home, announcements, policies, leave, shift schedule, services the administrator allowed).
2. **Given** 2 minutes of inactivity (configurable 1 to 15), **Then** he is signed out, and no cache, history, form draft or push subscription stays on the device.
3. **Given** kiosk mode, **Then** payslips (019), Confidential documents and mail (018) are not reachable, even by URL.
4. **Given** 5 wrong PINs, **Then** the badge is locked for 15 minutes on all kiosks and the user is notified.
5. **Given** a device administrator revokes the kiosk, **Then** it stops working within 1 minute.

### User Story 5: Use every page on a phone (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** any R1 or R2 page, **When** opened at 360 px in Arabic and English, **Then** there is no horizontal page scroll, no clipped text, and every action is reachable.
2. **Given** bottom navigation configured by the experience administrator, **Then** it shows up to 5 items with labels, and "More" holds the rest.

### Edge cases and exception flows
- iOS versions without web push: the app explains push is not available on this device and offers email.
- Low storage: the cache drops oldest items first and never blocks the app.
- Clock change on the device: offline expiry uses server time recorded at caching.
- Tenant host changes (custom domain): installed apps keep working through a redirect and prompt to reinstall.

## 5. Functional requirements

- **MOB-001 / FR-001**: TeamBeats MUST be an installable PWA with a manifest per tenant and brand from 033. *(EXP-009)*
- **MOB-002 / FR-002**: Updates MUST be versioned and applied on user action, never during unsaved input.
- **MOB-003 / FR-003**: Every R1 and R2 page MUST work at 360 px width in ar and en with no horizontal page scroll.
- **MOB-004 / FR-004**: Touch targets MUST be at least 44 by 44 points.
- **MOB-005 / FR-005**: Bottom navigation MUST show at most 5 configured items with labels.
- **MOB-006 / FR-006**: The push permission prompt MUST appear only after a meaningful action, and a decline MUST be remembered for 90 days.
- **MOB-007 / FR-007**: Push MUST be a 015 channel honoring user preferences, with no Confidential content in the payload.
- **MOB-008 / FR-008**: Push subscriptions MUST be deleted on sign-out, device revocation and gone responses.
- **MOB-009 / FR-009**: Offline caching MUST cover only targeted critical items, mandatory policies, saved and recent items, within tenant limits, encrypted per user.
- **MOB-010 / FR-010**: Confidential and higher items MUST NOT be cached offline.
- **MOB-011 / FR-011**: Offline caches MUST be wiped on sign-out and session expiry and pruned when access ends.
- **MOB-012 / FR-012**: Offline views MUST show an offline banner with the last update time.
- **MOB-013 / FR-013**: Mobile sessions MUST follow 026 device policy for lifetime and idle timeout.
- **MOB-014 / FR-014**: Kiosk devices MUST be enrolled by a device administrator and revocable within 1 minute.
- **MOB-015 / FR-015**: Kiosk sign-in MUST use badge code or QR plus PIN, with lockout after 5 failures.
- **MOB-016 / FR-016**: Kiosk sessions MUST end after inactivity and leave no personal data on the device.
- **MOB-017 / FR-017**: Kiosk mode MUST block features marked not for shared devices, enforced on the server.
- **MOB-018 / FR-018**: Deep links MUST open the target page in the installed app.
- **MOB-019 / FR-019**: The service worker MUST NOT cache API responses across users or tenants; cache keys MUST include tenant and user.
- **MOB-020 / FR-020**: Native shell apps (R3) MUST wrap the same PWA without separate features.

## 6. Business and validation rules
- Offline cache default 50 items and 50 MB; expiry 7 days; tenant range 0 to 200 items.
- Kiosk inactivity default 2 minutes; PIN 4 to 8 digits, set by the user on first kiosk use or from a reset service.
- Push payload title up to 60 characters, body up to 120.

## 7. Tenant and organization scope
- Tenant-owned data: manifests, kiosk devices, push subscriptions, offline policy.
- Organization scoping: kiosk devices belong to an org unit and location; kiosk features per device group.
- Cross-tenant denial cases: a kiosk of tenant A refuses badges of tenant B; service worker caches are partitioned by tenant host and user.
- Sibling, parent and child cases: a kiosk for Acme Gulf refuses Acme Africa users unless the administrator allows.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Push subscriptions | Product | Internal | Until sign-out or revocation | Yes |
| Kiosk devices | Product | Internal | Until revoked | No |
| Badge PIN hash | Product | Restricted | Until changed | Yes |
| Offline cache | Device, encrypted | Up to Internal | 7 days default | Yes |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `mobile.pwa` | on | on | on |
| `mobile.push` | on | on | on |
| `mobile.offline` | off | on | on |
| `mobile.kiosk` | off | on | on |
| `mobile.native_shell` (R3) | off | off | on |

## 10. UX, localization and accessibility
- Same components as the web with responsive layouts; no separate mobile code path.
- Screen reader and dynamic text size support; bottom navigation mirrors in RTL.

## 11. AI behavior
None specific. The assistant (014) is reachable on mobile with the same rules.

## 12. Integrations and events
- Push delivery through a Web Push adapter behind the 015 channel port (VAPID keys in the secret manager).
- Publishes `DeviceEnrolled`, `DeviceRevoked`, `PushSubscriptionCreated`, `PushSubscriptionRemoved`.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| device.kiosk_enrolled / revoked | audit | device, unit, actor |
| device.kiosk_signin / lockout | audit | device, user |
| mobile.offline_policy_changed | audit | limits, actor |
| mobile.install / push_optin | telemetry | platform (no device ID) |

## 14. Security and abuse cases
- Shared device leakage: no cache, wipe on sign-out, server-enforced feature block.
- Badge cloning: PIN required, lockout, revocation.
- Stolen phone: offline cache encrypted with a session-held key, session revocation wipes on next open, 7 day expiry.
- Push spoofing: VAPID signed, payloads carry no Confidential data.

## 15. Non-functional requirements
- Home loads under 3 seconds on a mid-range phone over 4G (Lighthouse mobile profile), app shell under 1 second when cached.
- Lighthouse PWA installability passes for every tenant host.

## 16. Success criteria
- **SC-001**: 50% of frontline employees in pilot tenants use the app weekly.
- **SC-002**: Zero personal data found on kiosk devices after sign-out in security tests.

## 17. Open questions
- [NEEDS CLARIFICATION: which badge format do frontline customers use?] Default: QR codes printed from TeamBeats plus manual employee number entry.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| EXP-009 | MOB-001 to MOB-005, US1, US5 |
| MOB-006 to MOB-008 | US2 |
| MOB-009 to MOB-012 | US3 |
| MOB-014 to MOB-017 | US4 |
