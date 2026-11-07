# Feature Specification: Notifications and Email

| Field | Value |
|---|---|
| Spec ID | 015-notifications |
| Area | platform |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (in-app notification centre, email through SMTP, user channel preferences with mandatory messages exempt, digest, templates per locale), R2 (Microsoft Teams, mobile push, digital signage feed, campaign sends for spec 009) |
| Last updated | 2026-11-07 |
| Requirement IDs | INT-008, INT-003 (Teams path, R2), NOT-001 to NOT-012 (new) |
| Use cases | UC-04 |
| Depends on | 004, 005, 006, 007, 008 (shell bell), 026 (no secrets in templates) |
| Used by | 009, 011, 013, 016, 020, 021, 022, 024, 025, 026, 027 |

## 1. Problem and outcome

**Problem:** Many approved specs say "the user is notified" (an approval is waiting, a request was returned, an announcement needs acknowledgement, an alert fired), but nothing delivers notifications. The old intranet's internal mail inbox is not something to copy.

**Outcome:** Modules decide what to say; this spec delivers it. Every user has an in-app notification centre with a bell in the header, and gets email for what they choose. Mandatory messages ignore preferences and are labelled. Messages are written from templates in the user's language and never carry secrets or more personal data than the recipient may see.

## 2. Scope

**In scope:**
- A send contract for modules: recipient users, category, mandatory flag, template key, parameters, deep link, deduplication key.
- In-app centre: list, unread count in the header, mark read, mark all read, open the deep link.
- Email over SMTP with a tenant sender name and a platform sending domain; tenant SMTP relay as an option. *(INT-008)*
- User preferences per category and channel (in-app always on; email on or off; daily digest option); mandatory categories cannot be turned off.
- Templates per category and locale (Arabic and English in R1), with a plain-text and HTML version.
- Delivery records, retries, bounces.

**Out of scope:**
- Teams, mobile push, digital signage (R2).
- Marketing email to people outside the tenant.
- Deciding who to notify (owning modules decide).

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Read and manage their notifications and preferences | Own only |
| Tenant administrator | Set sender name, SMTP relay, default preferences per category, mandatory categories within the platform list | `notifications.manage` |
| Module | Send through the contract as the system | System |

## 4. User scenarios

### User Story 1: See what needs me (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a module sends a notification, **Then** it appears in the recipient's centre within 5 seconds and the bell count goes up.
2. **Given** a notification, **When** the user opens it, **Then** it goes to the deep link and is marked read; the target still checks permissions.
3. **Given** a notification for another user or tenant, **Then** it is never visible to this user.
4. **Given** the same deduplication key sent twice within an hour, **Then** only one notification exists.

### User Story 2: Get email for what matters (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a category with email on, **Then** the user gets an email in their preferred language, with the tenant's sender name, a link into TeamBeats, and no content the user could not see in the product.
2. **Given** the user turns email off for a category, **Then** they get no email for it, unless the message is mandatory, which is labelled "mandatory".
3. **Given** the daily digest option, **Then** non-urgent emails for that category are combined into one email at the user's chosen time.
4. **Given** SMTP is down, **Then** emails retry with backoff for up to 24 hours, the in-app notification is not affected, and operators are alerted.
5. **Given** a hard bounce, **Then** email is paused for that address and the user sees a notice in the product.

### User Story 3: Administer notifications (Priority: P2, R1)

**Acceptance scenarios:**
1. **Given** the tenant sets a sender name and default preferences, **Then** new users get those defaults.
2. **Given** a tenant SMTP relay with credentials in the secret manager, **When** the administrator tests it, **Then** a test email is sent and the result shown; credentials are never shown again.

### Edge cases
- A recipient whose membership is disabled: nothing is sent.
- A burst of 100,000 notifications (critical announcement): in-app records are created within 2 minutes; emails are rate limited per the relay's limits.

## 5. Functional requirements

- **NOT-001 / FR-001**: Modules MUST send only through the notification contract; no module sends email directly. *(constitution: external systems through adapters)*
- **NOT-002 / FR-002**: Each notification MUST have a category, a mandatory flag, a template key and a deep link.
- **NOT-003 / FR-003**: The in-app centre MUST show the user's notifications with an unread count in the header.
- **NOT-004 / FR-004**: Email MUST be sent over SMTP through an adapter, with platform or tenant relay. *(INT-008)*
- **NOT-005 / FR-005**: Users MUST be able to turn email on or off per category and choose a daily digest; mandatory categories MUST be exempt and labelled.
- **NOT-006 / FR-006**: Templates MUST exist per locale; the user's preferred language decides, with the tenant default as fallback.
- **NOT-007 / FR-007**: Notification text MUST contain only what the recipient may see, and MUST NOT contain secrets, tokens or Restricted content.
- **NOT-008 / FR-008**: Delivery MUST retry with backoff for up to 24 hours and record the outcome.
- **NOT-009 / FR-009**: Deduplication keys MUST suppress repeats within 1 hour.
- **NOT-010 / FR-010**: Hard bounces MUST pause email for the address.
- **NOT-011 / FR-011**: Disabled members MUST receive nothing.
- **NOT-012 / FR-012**: Notifications MUST be kept 90 days by default.

## 6. Business and validation rules
- Platform mandatory categories: security notices, critical announcements, attestation due, approval escalations. Tenants may add more, not remove these.

## 7. Tenant and organization scope
- Notifications, preferences and delivery records are tenant-owned with RLS.
- Cross-tenant: a send with a user of another tenant is refused.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Notification | Product | Internal | 90 days | Yes |
| Preference | Product | Internal | Membership life | Yes |
| Delivery record | Product | Internal | 90 days | Yes (address) |

## 9. Package entitlements

| Capability key | Behavior |
|---|---|
| `notifications.core` | All packages: in-app and email |
| `notifications.tenant_smtp` | Workplace Plus and above: tenant SMTP relay |
| `notifications.teams` | R2 |

## 10. UX, localization and accessibility
- Bell in the header with a count announced to screen readers; centre as a panel and a page; RTL; email templates readable in plain text.

## 11. AI behavior
None. Agent briefings (spec 020, R2) send through this contract like any module.

## 12. Integrations and events
External system: SMTP through an adapter. Publishes `NotificationDelivered` and `NotificationBounced`.

## 13. Audit

| Event | Type | Key fields |
|---|---|---|
| notifications.settings_changed | audit | sender, relay host, defaults, actor (no secrets) |
| notifications.relay_tested | audit | result, actor |

## 14. Security and abuse cases
- Phishing look-alike emails: one platform sending domain with SPF, DKIM and DMARC; links only to the tenant host.
- Data leak through email: FR-007.

## 15. Non-functional requirements
- In-app delivery under 5 seconds p95; 100,000 in-app notifications within 2 minutes.

## 16. Success criteria
- **SC-001**: Every "notify" in R1 specs is delivered through this contract in the release suite.

## 17. Open questions
- [NEEDS CLARIFICATION: which email provider sends for shared SaaS?] Default: any SMTP relay set in platform configuration; the provider choice is an operations decision.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| INT-008 | NOT-004, US2 |
| UC-04 | US1, US2 |
