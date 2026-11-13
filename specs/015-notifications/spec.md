# Feature Specification: Notifications and Email

| Field | Value |
|---|---|
| Spec ID | 015-notifications |
| Area | platform |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (send contract for modules, in-app notification centre, email through SMTP, user channel preferences with mandatory messages exempt, grouping, daily digest, quiet hours, templates per locale, delivery records, retries and bounces), R2 (Microsoft Teams activity feed and bot messages, web push through spec 030, Slack direct messages through spec 017, digital signage feed, audience sends for spec 009 campaigns with delivery reports, SMS for mandatory messages to frontline staff without email) |
| Last updated | 2026-11-07 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | INT-008, INT-003 (Teams path, R2), NOT-001 to NOT-024 (new) |
| Use cases | UC-04 |
| Depends on | 003 (users and memberships), 004, 005, 006 (Connections area for Teams and SMTP relay), 007, 008 (shell bell), 017 (Slack, SMS gateway), 026 (no secrets in templates), 030 (push) |
| Used by | 009, 010, 011, 013, 016, 019, 020, 021, 022, 023, 024, 025, 026, 027, 028, 029, 031 |

## 1. Problem and outcome

**Problem:** Many approved specs say "the user is notified" (an approval is waiting, a request was returned, an announcement needs acknowledgement, an alert fired), but nothing delivers notifications. The old intranet's internal mail inbox is not something to copy. Without one service, each module would send its own emails, ignore preferences, flood people, and leak content into mail that the recipient may not be allowed to see.

**Outcome:** Modules decide what to say and who to tell; this spec delivers it. Every user has an in-app notification centre with a bell in the header, and gets email, Teams, push or Slack for what they choose. Mandatory messages ignore preferences and are labelled. Repeated events are grouped, quiet hours are respected for anything not urgent, and messages are written from templates in the user's language and never carry secrets or more than the recipient may see.

## 2. Scope

**In scope:**
- A send contract for modules: recipient users (or an audience reference resolved by the owning module), category, urgency (normal, urgent), mandatory flag, template key, parameters, deep link, deduplication key, group key.
- In-app centre: list, unread count in the header, filters (unread, mandatory, by category), mark read, mark all read, open the deep link.
- Email over SMTP with a tenant sender name and a platform sending domain; tenant SMTP relay as an option. *(INT-008)*
- User preferences per category and channel (in-app always on; email, Teams, push and Slack on or off; daily digest; quiet hours); mandatory categories cannot be turned off.
- Grouping: notifications with the same group key within a window become one entry with a count ("5 new comments on…").
- Templates per category and locale (Arabic and English in R1, French, Urdu and Hindi in R2), with plain-text and HTML versions, tenant overrides of wording within platform templates.
- Delivery records per channel, retries, bounces, and per-send delivery reports for owners of audience sends.
- R2 channels: Microsoft Teams (activity feed notification and bot message through the Microsoft 365 connection of spec 006), web push (030), Slack direct message (017), SMS for mandatory messages (017 SMS gateway), digital signage feed (a read-only signed feed of selected announcements per location).

**Out of scope:**
- Marketing email to people outside the tenant.
- Deciding who to notify (owning modules decide and resolve audiences).
- Two-way chat; replies from Teams or Slack other than approve and reject actions defined by 016 in R2.

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Read and manage their notifications, preferences and quiet hours | Own only |
| Tenant administrator | Set sender name, SMTP relay, default preferences per category, mandatory categories within the platform list, template wording overrides, channels enabled | `notifications.manage` |
| Communicator | See delivery reports for audience sends they made (spec 009) | Owner of the send |
| Location administrator | Configure signage screens and which categories they show | `notifications.signage_manage` in location scope |
| Module | Send through the contract as the system | System |

## 4. User scenarios

### User Story 1: See what needs me (Priority: P1, R1)

**Why this priority:** every other spec depends on delivery.

**Independent test:** send from a test module to three users in two tenants; check the centre, count, deep link, permission check on open, and deduplication.

**Acceptance scenarios:**
1. **Given** a module sends a notification, **Then** it appears in the recipient's centre within 5 seconds and the bell count goes up without a page reload.
2. **Given** a notification, **When** the user opens it, **Then** it goes to the deep link and is marked read; the target still checks permissions, and if access was lost the user sees "no longer available".
3. **Given** a notification for another user or tenant, **Then** it is never visible to this user.
4. **Given** the same deduplication key sent twice within an hour, **Then** only one notification exists.
5. **Given** ten comments on a post Laila follows within 15 minutes with one group key, **Then** she sees one entry "10 new comments" that opens the post.

### User Story 2: Get email for what matters (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a category with email on, **Then** the user gets an email in their preferred language, with the tenant's sender name, a link into TeamBeats, and no content the user could not see in the product.
2. **Given** the user turns email off for a category, **Then** they get no email for it, unless the message is mandatory, which is labelled "mandatory".
3. **Given** the daily digest option, **Then** non-urgent emails for that category are combined into one email at the user's chosen time.
4. **Given** SMTP is down, **Then** emails retry with backoff for up to 24 hours, the in-app notification is not affected, and operators are alerted.
5. **Given** a hard bounce, **Then** email is paused for that address and the user sees a notice in the product.

### User Story 3: Keep my evenings quiet (Priority: P2, R1)

**Acceptance scenarios:**
1. **Given** quiet hours 20:00 to 07:00 in Laila's time zone, **When** a normal notification arrives at 22:00, **Then** in-app shows it at once and other channels hold it until 07:00.
2. **Given** an urgent mandatory message (for example a site emergency), **Then** it is delivered on every enabled channel at once regardless of quiet hours.
3. **Given** the tenant sets working days per location (022 calendars), **Then** default quiet hours follow them until the user changes them.

### User Story 4: Administer notifications (Priority: P2, R1)

**Acceptance scenarios:**
1. **Given** the tenant sets a sender name and default preferences, **Then** new users get those defaults, and existing users keep their own choices.
2. **Given** a tenant SMTP relay with credentials in the secret manager, **When** the administrator tests it, **Then** a test email is sent and the result shown; credentials are never shown again.
3. **Given** a template wording override in Arabic, **Then** it is previewed with sample parameters before saving, and parameters the template needs cannot be removed.

### User Story 5: Get notified in Teams (Priority: P1, R2)

Acme works in Microsoft Teams all day.

**Acceptance scenarios:**
1. **Given** the Teams feature of the Microsoft 365 connection is on and the TeamBeats app is installed for users, **When** Laila has Teams on for approvals, **Then** she gets a Teams activity notification that opens the item in TeamBeats inside Teams.
2. **Given** an approval (016) with quick actions allowed, **Then** the Teams message offers approve and reject, which call the same 016 endpoints with Laila's identity and the same checks, and step-up is requested in TeamBeats when the route requires it.
3. **Given** the Teams connection is disabled or the app is uninstalled, **Then** delivery falls back to the user's other channels and the delivery record says why.

### User Story 6: Reach phones and frontline staff (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** Laila enabled push on her phone (030), **Then** notifications in categories with push on arrive there with no Confidential content.
2. **Given** Omar has no email and no app, **When** a mandatory message is sent and SMS is enabled for the tenant, **Then** he gets an SMS with a short text and a link, at most one SMS per message.
3. **Given** a Slack connection (017) and a linked Slack account, **Then** categories with Slack on are delivered as direct messages.

### User Story 7: Show announcements on screens (Priority: P3, R2)

**Acceptance scenarios:**
1. **Given** a signage screen registered to the Riyadh warehouse, **Then** it shows published announcements (009) targeted to that location and marked for signage, in rotation, through a signed feed URL that reveals nothing else.
2. **Given** an announcement is withdrawn, **Then** it leaves the screen within 1 minute.
3. **Given** the feed URL is revoked, **Then** the screen stops showing content within 1 minute.

### User Story 8: Know who received a send (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a communicator sent a critical announcement to 5,000 people, **Then** the delivery report shows counts by channel for delivered, read, failed and bounced, without listing names unless the announcement requires acknowledgement (009 then lists who has not acknowledged).

### Edge cases and exception flows
- A recipient whose membership is disabled: nothing is sent; queued items for them are dropped.
- A burst of 100,000 notifications (critical announcement): in-app records are created within 2 minutes; emails, Teams and SMS are rate limited to provider limits.
- A template missing for the user's language: the tenant default language is used and the gap is logged.
- A deep link to a deleted item: the entry stays with "no longer available".
- A user with two memberships in one tenant: one centre, preferences per membership type are not supported; preferences are per user.

## 5. Functional requirements

- **NOT-001 / FR-001**: Modules MUST send only through the notification contract; no module sends email, Teams, push, Slack or SMS directly. *(constitution: external systems through adapters)*
- **NOT-002 / FR-002**: Each notification MUST have a category, urgency, mandatory flag, template key and deep link.
- **NOT-003 / FR-003**: The in-app centre MUST show the user's notifications with an unread count in the header that updates live.
- **NOT-004 / FR-004**: Email MUST be sent over SMTP through an adapter, with platform or tenant relay. *(INT-008)*
- **NOT-005 / FR-005**: Users MUST be able to turn each channel except in-app on or off per category and choose a daily digest; mandatory categories MUST be exempt and labelled.
- **NOT-006 / FR-006**: Templates MUST exist per locale; the user's preferred language decides, with the tenant default as fallback.
- **NOT-007 / FR-007**: Notification text on every channel MUST contain only what the recipient may see, and MUST NOT contain secrets, tokens or Restricted content; push, SMS and signage MUST NOT contain Confidential content.
- **NOT-008 / FR-008**: Delivery MUST retry with backoff for up to 24 hours and record the outcome per channel.
- **NOT-009 / FR-009**: Deduplication keys MUST suppress repeats within 1 hour.
- **NOT-010 / FR-010**: Hard bounces MUST pause email for the address.
- **NOT-011 / FR-011**: Disabled members MUST receive nothing.
- **NOT-012 / FR-012**: Notifications MUST be kept 90 days by default.
- **NOT-013 / FR-013**: Notifications with the same group key within the grouping window (default 15 minutes) MUST be shown as one entry with a count.
- **NOT-014 / FR-014**: Quiet hours MUST hold non-urgent deliveries on channels other than in-app; urgent mandatory messages MUST bypass them.
- **NOT-015 / FR-015**: Opening a notification MUST re-check access to the target.
- **NOT-016 / FR-016**: Template overrides MUST be previewed and MUST keep required parameters.
- **NOT-017 / FR-017**: Teams delivery MUST use the Microsoft 365 connection of spec 006 and fall back to other channels when unavailable. *(INT-003)*
- **NOT-018 / FR-018**: Quick actions from Teams MUST call the owning module's endpoints with the user's identity and the same authorization and step-up checks.
- **NOT-019 / FR-019**: Push MUST go through the spec 030 subscription model; Slack and SMS MUST go through spec 017 connectors.
- **NOT-020 / FR-020**: SMS MUST be limited to mandatory messages and to one SMS per message per user.
- **NOT-021 / FR-021**: Signage feeds MUST be signed, revocable within 1 minute, and show only announcements marked for signage and targeted to the screen's location.
- **NOT-022 / FR-022**: Delivery reports MUST show counts by channel and status, and names only where the owning spec requires acknowledgement tracking.
- **NOT-023 / FR-023**: Sends MUST be accepted only for recipients in the sender's tenant; the tenant comes from the sending context, never from parameters.
- **NOT-024 / FR-024**: The in-app centre MUST work with keyboard and screen readers and announce new notifications politely.
- **FR-025**: A notification that asks the person to act on something (an approval waiting, a task assigned) MUST be resolved when the thing is done or no longer theirs: when the task is completed by anyone, reassigned, or its request is withdrawn or ends, the owning module tells this module and the notification is marked read and leaves the unread count; pending emails for it are dropped. The bell's approvals and the task badge then agree, because both reflect open tasks. Acceptance: approving a task as one approver resolves the "approval waiting" notification of every other named approver; withdrawing a request resolves its waiting notifications; a test checks that a resolve call in one tenant never touches another tenant's notifications.

## 6. Business and validation rules
- Platform mandatory categories: security notices, critical announcements, attestation due, approval escalations, site emergencies. Tenants may add more, not remove these.
- Grouping window 15 minutes; digest time default 08:00 in the user's time zone.
- Default quiet hours: off, or the location's non-working hours when the tenant turns that on.
- Retry: email 24 hours, Teams and Slack 2 hours, push 1 hour, SMS 30 minutes.

## 7. Tenant and organization scope
- Tenant-owned data: notifications, preferences, quiet hours, templates overrides, delivery records, signage screens.
- Organization scoping: signage screens belong to a location; communicators see reports only for their own sends.
- Cross-tenant denial cases: a send with a user of another tenant is refused; Teams and Slack identities map to one tenant user only; signage feed URLs resolve to one tenant.
- Sibling, parent and child cases: a location administrator of Acme Gulf cannot register screens for Acme Africa locations.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Notification | Product | Internal | 90 days | Yes |
| Preference and quiet hours | Product | Internal | Membership life | Yes |
| Delivery record | Product | Internal | 90 days | Yes (address) |
| Template overrides | Product | Internal | Life of tenant | No |
| Signage screen | Product | Internal | Until removed | No |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `notifications.core` (in-app, email, digest, quiet hours) | on | on | on |
| `notifications.tenant_smtp` | off | on | on |
| `notifications.teams` (R2) | on | on | on |
| `notifications.push` (R2) | on | on | on |
| `notifications.slack` (R2) | off | on | on |
| `notifications.sms` (R2, usage limit) | off | off | on |
| `notifications.signage` (R2) | off | on | on |

## 10. UX, localization and accessibility
- Bell in the header with a count announced to screen readers; centre as a panel and a page; preferences page as a grid of categories by channels; RTL; email templates readable in plain text.

## 11. AI behavior
None. Agent briefings (spec 020, R2) send through this contract like any module.

## 12. Integrations and events
- External systems: SMTP through an adapter; Teams through the Microsoft 365 connection; push, Slack and SMS through 030 and 017 adapters.
- Publishes `NotificationDelivered`, `NotificationRead`, `NotificationBounced`.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| notifications.settings_changed | audit | sender, relay host, defaults, actor (no secrets) |
| notifications.relay_tested | audit | result, actor |
| notifications.template_overridden | audit | template, locale, actor |
| notifications.signage_screen_registered / revoked | audit | screen, location, actor |
| notifications.delivery | telemetry | category, channel, status, latency (no content) |

## 14. Security and abuse cases
- Phishing look-alike emails: one platform sending domain with SPF, DKIM and DMARC; links only to the tenant host.
- Data leak through email or lock screens: FR-007.
- Forged quick actions from Teams: actions are signed per user and message, expire after 7 days, and run the owning module's checks.
- Signage screen theft: feed revocation and location-only content.
- Notification flooding by a faulty module: per module rate limits and grouping.

## 15. Non-functional requirements
- In-app delivery under 5 seconds p95; 100,000 in-app notifications within 2 minutes.
- The unread count in the shell refreshes within 30 seconds while the page is visible, and at once when the page becomes visible again.

## 16. Success criteria
- **SC-001**: Every "notify" in R1 specs is delivered through this contract in the release suite.
- **SC-002**: Fewer than 5% of users turn off email for all non-mandatory categories after 3 months in pilot tenants.

## 17. Open questions
- [NEEDS CLARIFICATION: which email provider sends for shared SaaS?] Default: any SMTP relay set in platform configuration; the provider choice is an operations decision.
- [NEEDS CLARIFICATION: which SMS gateway?] Default: a generic SMS REST pack through 017; the provider is chosen per tenant.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| INT-008 | NOT-004, US2 |
| INT-003 | NOT-017, NOT-018, US5 |
| UC-04 | US1, US2, US8 |
