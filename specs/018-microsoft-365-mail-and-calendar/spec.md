# Feature Specification: Microsoft 365 Mail, Calendar and Rooms

| Field | Value |
|---|---|
| Spec ID | 018-microsoft-365-mail-and-calendar |
| Area | integrations |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R2 (Microsoft 365 connection features for mail and calendar, My mail and My calendar home components, unread badge, colleague free and busy, meeting join links, room finder and booking, calendar for the assistant), R3 (reply to invitations from TeamBeats, mail summaries by the assistant, Teams presence, Google Workspace mail and calendar through the same ports) |
| Last updated | 2026-11-05 |
| Requirement IDs | INT-001, INT-002, INT-009, UC-10 (mail and calendar part), M365-001 to M365-024 (new) |
| Use cases | UC-10 (mail and calendar part) |
| Depends on | 003 (Entra ID sign-in and consent), 004, 005, 006 (admin connections), 007, 008 (home and shell), 010 (people profile), 011 (SharePoint through the same connection), 013, 014 and 020 (assistant and agents), 015 (notifications), 017 (connector framework), 021, 022 (leave and holidays), 026, 027 (agent tools), 032 (component manifests) |

## 1. Problem and outcome

**Problem:** Employees start their day in two places: the intranet for news, tasks and services, and Outlook for mail and meetings. The current intranet shows neither their mail nor their calendar, so the home page is never the single place to start. Admins also have no safe way to switch this on: giving a product wide mailbox access is a security finding in every regulated customer.

**Outcome:** A tenant admin turns on Mail and Calendar on the tenant's Microsoft 365 connection in admin connections. Each employee then sees their own latest Outlook mail and their upcoming appointments on the home page, joins a Teams meeting in one click, checks whether a colleague is free, and finds and books a room, all read through Microsoft Graph as the signed-in employee with the least permissions that do the job. TeamBeats never reads anyone's mailbox with tenant-wide permissions, never stores mail or event content, and keeps working with a clear message when Microsoft 365 is down or the employee has no mailbox.

## 2. Scope

**In scope:**
- Mail, Calendar, Free and busy and Rooms as features of the tenant's Microsoft 365 connection in admin connections (spec 006 US5, FR-010 to FR-015). The same connection serves directory sync (spec 003) and SharePoint documents (spec 011); this spec adds features to it, not a second connection.
- Delegated access as the signed-in employee through the Entra ID sign-in of spec 003, with incremental consent and the least-privilege Microsoft Graph permissions per feature.
- My mail home component: unread count, latest messages from the Inbox (Focused or all), open in Outlook, new message in Outlook.
- Unread mail badge as an optional shell utility item (spec 008).
- My calendar home component: today and the next days, all-day items, meeting join links, open in Outlook, and TeamBeats items in the same view (registered events from 009, approved leave and public holidays from 022), labelled by source.
- Colleague free and busy on the profile card (spec 010) and in people pickers that schedule something.
- Room finder and booking for Microsoft 365 room mailboxes (the 018 part referenced by specs 022 and 031).
- Calendar access for the assistant and the Personal Productivity agent (specs 014, 020) as read tools under spec 027, with a separate opt-in for access while the employee is signed out.
- Canonical mailbox and calendar ports so a Google Workspace adapter can plug in later without changing product modules (INT-004).

**Out of scope:**
- A mail client inside TeamBeats: reading full message bodies, writing, replying, forwarding, moving, deleting or flagging mail. Those open in Outlook.
- Shared and delegated mailboxes and calendars of other people (beyond free and busy).
- On-premises Exchange Server through EWS. Hybrid mailboxes that Microsoft Graph serves are covered.
- SharePoint Online document libraries (spec 011, through the same connection).
- Teams channel notifications and posting (spec 015) and Teams presence (R3, open question).
- Workplace booking for tenants without Microsoft 365 rooms (spec 031).
- OneDrive personal files (open question).
- Directory and org mapping from Entra ID (specs 002, 003).

## 3. Actors and permissions

| Actor | What they can do in this feature | Permission / scope required |
|---|---|---|
| Tenant administrator or Integration Administrator | Turn connection features on or off, choose options (preview line, free and busy, rooms, assistant access), see consent status and health | `integration.manage` (spec 004, spec 006 FR-010 to FR-015) |
| Microsoft 365 administrator (customer side) | Grants admin consent for the delegated permissions in Entra ID, or lets users consent | Outside TeamBeats (Entra ID role) |
| Page editor | Places My mail and My calendar on pages they edit, within the region rules | `page.edit` (spec 032) |
| Employee | Sees only their own mail and calendar, connects or disconnects their account, checks colleagues' free and busy, books rooms | Active tenant membership with a Microsoft 365 mailbox; feature on |
| Assistant and agents | Read the employee's calendar (R2) and mail summaries (R3) only as that employee, after opt-in | Spec 027 tool grant plus the employee's consent |

Permission keys follow spec 004 FR-013. No new permission key is needed for employees: access to a mailbox is decided by Microsoft 365 for the signed-in user, never by a TeamBeats role.

## 4. User scenarios

### User Story 1: Admin turns on mail and calendar for the tenant (Priority: P1, R2)

Huda, tenant admin for Acme Gulf, opens Admin, Connections, Microsoft 365. Directory sync and SharePoint are already on. She turns on Mail and Calendar. The page lists exactly what each feature asks Microsoft for, as delegated permissions, and a "Grant admin consent" button that opens the Microsoft consent page for the customer's Microsoft 365 admin. After consent, the connection health shows Mail and Calendar as Ready.

**Why this priority:** nothing else in this spec works until the connection feature is on and consented.

**Independent test:** with a test Entra tenant, turn Mail on and off, grant and revoke consent, and check the health, the audit entries and the home component state after each step.

**Acceptance scenarios:**
1. **Given** a tenant with `integration.microsoft365` on and a Microsoft 365 connection, **When** the admin opens the connection, **Then** Mail, Calendar, Free and busy and Rooms are listed as features, each off by default, each with the delegated permissions it needs in plain words. *(M365-001)*
2. **Given** the admin turns on Mail with the preview line option off, **Then** the permissions requested are `User.Read` and `Mail.ReadBasic` only. **Given** the preview line option on, **Then** `Mail.Read` replaces `Mail.ReadBasic`, and the page says that this lets TeamBeats read message text. *(M365-002)*
3. **Given** the admin turns on Calendar, **Then** the permission requested is `Calendars.ReadBasic` (events without body and attachments). Rooms adds `Place.Read.All` and `Calendars.ReadWrite`, and the page says that booking writes to the employee's own calendar. *(M365-002)*
4. **Given** any feature, **Then** TeamBeats MUST NOT request application permissions (such as `Mail.Read` as an application) for mail or calendar, and the connection test fails with a clear reason if the app registration has them. *(M365-003)*
5. **Given** admin consent has not been granted and tenant policy blocks user consent, **When** an employee opens the home page, **Then** My mail shows "Your administrator has not finished setting up Outlook in TeamBeats" and no consent prompt is shown to the employee. *(M365-004)*
6. **Given** the tenant does not have `integration.microsoft365`, **Then** the features are not offered, the components are not in the picker, and existing placements show "not available in your plan" to admins only and are hidden for employees. *(M365-019)*
7. **Given** the admin turns Mail off, **Then** within 1 minute all My mail placements show "Outlook mail is turned off by your administrator", no further Graph mail calls are made, and stored delegated tokens that only served mail are dropped. *(M365-005)*

### User Story 2: My mail on the home page (Priority: P1, R2)

Omar opens TeamBeats in the morning. Next to his tasks he sees "My mail: 7 unread" and his five latest Focused Inbox messages with sender, subject, time, and markers for unread, high importance and attachments. He clicks one and it opens in Outlook on the web in a new tab. He clicks "New message" and Outlook opens a new draft.

**Why this priority:** this is the main ask: the home page as the place to start the day.

**Independent test:** with a seeded test mailbox, check count, order, markers, links, RTL rendering, and each failure state by fault injection.

**Acceptance scenarios:**
1. **Given** Mail is on and the employee has a mailbox, **When** the home loads, **Then** My mail shows the unread count of the Inbox and the latest messages (default 5, up to 10 by placement setting), newest first, with sender name, subject, received time in the employee's time zone and locale (spec 007), and unread, importance and attachment markers that are not color alone. *(M365-006)*
2. **Given** the employee uses Focused Inbox, **Then** the component shows Focused by default with a switch to All; the choice is a personal preference (spec 008 FR-021). *(M365-006)*
3. **Given** a message in the list, **When** the employee activates it by pointer or keyboard, **Then** it opens in Outlook on the web in a new tab using the link Microsoft returns; TeamBeats does not render the message body. *(M365-007)*
4. **Given** the preview line option is on, **Then** each message shows the first line of text that Microsoft provides as plain text, never as HTML, with no remote images or links followed. *(M365-008)*
5. **Given** the employee reads mail in Outlook, **When** they return to TeamBeats, **Then** the count and markers are current within 2 minutes, or at once on refresh. *(M365-009)*
6. **Given** the employee has no Exchange Online mailbox (for example a frontline user without a licence), **Then** the component says "You don't have a work mailbox" and the add panel stops offering it to them; nothing else on the page changes. *(M365-010)*
7. **Given** Microsoft Graph is slow or down, **Then** the component shows the degraded state with the last count it showed this session and its age, and a retry; it never blocks the page (constitution XVII, spec 032 FR-029). *(M365-011)*
8. **Given** conditional access blocks the token (for example an unmanaged device), **Then** the component says "Your organization requires you to open Outlook on a managed device" with a link to Outlook, and does not loop the sign-in. *(M365-012)*
9. **Given** the employee clicks "Disconnect Outlook" in their settings, **Then** TeamBeats deletes its delegated token for mail and calendar for that membership, the components show a "Connect Outlook" action, and the action is audited. *(M365-013)*

### User Story 3: My calendar on the home page (Priority: P1, R2)

Mona sees today's appointments: a 09:30 Teams meeting with a Join button that lights up 10 minutes before it starts, a 13:00 room meeting, and below them "Tomorrow" and the next days. The same view shows the company town hall she registered for in TeamBeats and her approved leave next Thursday, each with its source label.

**Why this priority:** the second main ask, and the one employees use most during the day.

**Independent test:** seeded mailbox with recurring, all-day, overlapping, cancelled, private and online events across a time zone change; check the view in English and Arabic.

**Acceptance scenarios:**
1. **Given** Calendar is on, **Then** My calendar shows the employee's own events for today and the next days (default 7, placement setting 1 to 14), with recurring series expanded into occurrences, all-day items first, times in the employee's time zone, and the location or "Online". *(M365-014)*
2. **Given** an online meeting, **Then** a Join action opens the meeting link Microsoft returns; it is the primary action from 10 minutes before the start until the end. *(M365-015)*
3. **Given** an event, **When** the employee activates it, **Then** it opens in Outlook on the web. Responses to invitations (accept, tentative, decline) are done in Outlook in R2 and from TeamBeats in R3. *(M365-015)*
4. **Given** events that are cancelled, declined by the employee or marked free, **Then** cancelled and declined events are hidden, and free events are shown with a "free" marker. Tentative events are marked as tentative in text. *(M365-014)*
5. **Given** a private event in the employee's own calendar, **Then** its subject is shown to the employee, and a placement setting "hide subjects of private events" shows "Private appointment" instead, for screens others can see. *(M365-016)*
6. **Given** TeamBeats items for the same days, **Then** registered events (spec 009), approved leave and public holidays of the employee's location (spec 022) appear in the same list with a source label; an item that spec 022 already exported to Outlook as an out-of-office event is shown once, as the TeamBeats item. *(M365-017)*
7. **Given** Arabic, **Then** the view is mirrored, times use the tenant digit setting, and the optional Hijri secondary date of spec 007 applies to day headers. *(M365-014)*
8. **Given** Microsoft Graph is unavailable, **Then** TeamBeats items still show, the Outlook part shows the degraded state with the age of the last data this session, and the page stays usable. *(M365-011)*

### User Story 4: Check whether a colleague is free (Priority: P2, R2)

Karim opens Sara's profile card to ask a quick question. It shows "Busy until 11:00, free from 11:00 to 12:30" for today, from her Microsoft 365 free and busy. He can open a new Outlook meeting with her from the card.

**Independent test:** two seeded mailboxes, one with a busy block and one with an out-of-office block; check the card for a user in the same tenant and for a user who lacks permission to see the profile.

**Acceptance scenarios:**
1. **Given** Free and busy is on, **When** an employee views a colleague's profile card (spec 010), **Then** it shows the colleague's status for the rest of today (free, busy, tentative, out of office, working elsewhere) as text, without subjects, locations or attendees. *(M365-018)*
2. **Given** the viewer cannot see the colleague's profile in TeamBeats (spec 004), **Then** no free and busy is requested or shown. Free and busy never reveals more than Outlook itself shows the viewer. *(M365-018)*
3. **Given** the colleague's mailbox does not share free and busy, **Then** the card shows nothing for availability rather than an error. *(M365-018)*
4. **Given** a people picker in a TeamBeats form that schedules something (for example an HR appointment in spec 016), **Then** it MAY show the same free and busy for the chosen people and day. *(M365-018)*

### User Story 5: Find and book a room (Priority: P2, R2)

Ahmed needs a room for six people tomorrow at 14:00 in the Dubai office. He opens Rooms, filters by building, capacity and equipment, sees which rooms are free at that time, and books one. The booking is a meeting in his own Outlook calendar with the room as a resource, and the room mailbox accepts or declines it under its own rules.

**Independent test:** seeded room list and room mailboxes with one auto-accept room and one that needs a delegate; book both; check the result in the employee's calendar.

**Acceptance scenarios:**
1. **Given** Rooms is on, **Then** the room finder lists the tenant's Microsoft 365 room lists and rooms with building, floor, capacity and equipment, filtered by the employee's office location by default (spec 010). *(M365-020)*
2. **Given** a date, time and duration, **Then** each room shows whether it is free, from the rooms' free and busy. *(M365-020)*
3. **Given** the employee books a free room, **Then** TeamBeats creates an event in the employee's own calendar with the room as a resource attendee and the subject they entered; the room's acceptance or decline appears in Outlook and in My calendar. TeamBeats never writes to the room mailbox directly. *(M365-021)*
4. **Given** two employees book the same room at once, **Then** the room mailbox decides, and the employee whose booking is declined sees "The room was not available" in My calendar. *(M365-021)*
5. **Given** a tenant without Microsoft 365 rooms, **Then** the room finder is not offered and spec 031 applies. *(M365-020)*

### User Story 6: Ask the assistant about my day (Priority: P2, R2 calendar, R3 mail)

Mona asks the assistant "When is my next meeting and what's free this afternoon?" and gets the answer from her own calendar with a link to each event. The daily briefing of the Personal Productivity agent (spec 020 US7) can include her meetings for the day if she opts in.

**Independent test:** assistant calls with the feature on and off, with and without the employee's opt-in, with a prompt-injection string in an event subject.

**Acceptance scenarios:**
1. **Given** the tenant allows the calendar tool for the assistant (spec 027) and the employee has a TeamBeats session, **When** they ask about their calendar, **Then** the assistant reads only that employee's calendar as that employee and cites each event with a link. *(M365-022)*
2. **Given** the daily briefing runs while the employee is signed out, **Then** it includes calendar items only if the employee opted in to "Let TeamBeats read my calendar when I'm not signed in"; otherwise the briefing says "Sign in to see today's meetings". *(M365-023)*
3. **Given** an event subject that contains instructions to the AI, **Then** it is treated as untrusted data and cannot trigger a tool or change the answer's scope (constitution AI rules, spec 027). *(M365-022)*
4. **Given** R3 mail summaries, **Then** they require a separate tenant switch and a separate employee opt-in, use only messages in the employee's own Inbox, and follow the classification and provider rules of spec 013 for Confidential data. *(M365-022)*

### Edge cases and exception flows
- The employee signed in to TeamBeats with one Entra account and has no mailbox in that Microsoft 365 tenant (for example a B2B guest): mail and calendar are not offered.
- A tenant has two Microsoft 365 tenants (after a merger): one Microsoft 365 connection per Entra tenant; each employee's mailbox is read through the connection of the issuer they signed in with (spec 003 FR-003).
- The user's mailbox is moved between on-premises and Exchange Online: while Graph cannot serve it, the component shows "Your mailbox is not available right now" and links to Outlook.
- Consent is revoked in Entra ID: the next call fails with a consent error; the component shows the admin message of US1 scenario 5, and connection status turns to "needs consent" (spec 006).
- Graph throttles the tenant (HTTP 429): requests back off as Microsoft asks, the component keeps its last data with its age, and connection status shows "degraded" (spec 006).
- The employee's time zone in TeamBeats differs from Outlook's: TeamBeats shows times in the TeamBeats preference (spec 007) and says so in the component settings.
- Very long subjects, right-to-left subjects in an English page and mixed scripts: truncated with full text available to screen readers, direction per text run.
- A membership is disabled by directory sync (spec 003 FR-013): the employee's delegated tokens are deleted in the same run.
- Tenant switch in one browser: tokens, caches and components belong to the tenant membership; switching tenant never shows the other tenant's mail.

## 5. Functional requirements

### Connection features
- **FR-001 / M365-001**: Mail, Calendar, Free and busy and Rooms MUST be features of the tenant's Microsoft 365 connection in admin connections (spec 006 US5, FR-010 to FR-015), each off by default, each listing its Microsoft Graph permissions in plain words before consent. Adding a feature MUST ask only for the added consent. *(INT-001, INT-002)*
- **FR-002 / M365-002**: Each feature MUST request only the delegated permissions it needs: Mail `User.Read` and `Mail.ReadBasic` (or `Mail.Read` when the preview line option is on); Calendar `Calendars.ReadBasic`; Free and busy `Calendars.ReadBasic`; Rooms `Place.Read.All` and `Calendars.ReadWrite`; plus `offline_access` only when an employee opts in under FR-023.
- **FR-003 / M365-003**: Mail and calendar MUST NOT use application permissions or any tenant-wide mailbox access. The connection test MUST warn when the app registration holds application mail or calendar permissions.
- **FR-004 / M365-004**: The system MUST support admin consent and, where tenant policy allows, incremental user consent at first use. When consent is missing, employees MUST see an admin-setup message, not a consent loop.
- **FR-005 / M365-005**: Turning a feature off MUST stop its Graph calls and update every placement within 1 minute (spec 006), and delete delegated tokens that no remaining feature needs.

### My mail
- **FR-006 / M365-006**: My mail MUST show the Inbox unread count and the latest messages (default 5, maximum 10), with sender, subject, received time and text markers for unread, importance and attachments; Focused or All MUST be a personal preference.
- **FR-007 / M365-007**: Opening a message or starting a new one MUST go to Outlook on the web through links Microsoft returns. TeamBeats MUST NOT render message bodies or attachments.
- **FR-008 / M365-008**: When the preview line is on, preview text MUST be shown as plain text only, never as HTML, and no remote content MUST be loaded.
- **FR-009 / M365-009**: Counts and lists MUST be no older than 2 minutes while the page is open and refresh on demand. The system MAY use Graph change notifications to update sooner.
- **FR-010 / M365-010**: Employees without a mailbox MUST see a clear "no work mailbox" state, and the add panel MUST stop offering mail and calendar components to them.
- **FR-011 / M365-011**: When Microsoft Graph is unavailable or throttling, components MUST show the degraded state with the age of the last data shown in the session and a retry, and MUST NOT block the page. *(INT-009, constitution XVII)*
- **FR-012 / M365-012**: Conditional access and MFA failures MUST be shown as an explanation with a link to Outlook, never as a sign-in loop, and MUST NOT be bypassed (spec 003 FR-009).
- **FR-013 / M365-013**: Employees MUST be able to disconnect and reconnect their Outlook from their settings; disconnect MUST delete TeamBeats' delegated tokens for that membership.

### My calendar
- **FR-014 / M365-014**: My calendar MUST show the employee's own events for a configurable range (default 7 days, 1 to 14), recurring series expanded, cancelled and declined events hidden, tentative and free marked in text, times in the TeamBeats time zone and locale, mirrored in RTL.
- **FR-015 / M365-015**: Online meetings MUST offer a Join action from Microsoft's join link, primary from 10 minutes before start until end. Events MUST open in Outlook on the web. Responding to invitations from TeamBeats is R3 and needs `Calendars.ReadWrite`.
- **FR-016 / M365-016**: A placement setting MUST allow hiding subjects of private events.
- **FR-017 / M365-017**: My calendar MUST merge TeamBeats items (registered events from 009, approved leave and location holidays from 022) labelled by source, and MUST show an item exported to Outlook by TeamBeats only once.

### Free and busy, rooms
- **FR-018 / M365-018**: Profile cards and scheduling people pickers MUST show a colleague's free and busy for the day as status text only, only when the viewer may see that profile in TeamBeats, and never more than Microsoft 365 shows that viewer.
- **FR-019 / M365-019**: All features and components MUST be gated by `integration.microsoft365` (spec 005); without it they are not offered and existing placements are hidden from employees.
- **FR-020 / M365-020**: Rooms MUST list Microsoft 365 room lists and rooms with building, floor, capacity and equipment, default to the employee's office, and show free or busy for a chosen slot.
- **FR-021 / M365-021**: Booking MUST create an event in the employee's own calendar with the room as a resource attendee; the room mailbox's own rules decide acceptance, and the result MUST be visible in My calendar.

### Assistant and agents
- **FR-022 / M365-022**: The assistant and agents MAY read calendar data (R2) and mail summaries (R3) only through read tools registered under spec 027, only as the employee, only when the tenant allows the tool and, for mail, the employee has opted in. Mail and event text MUST be treated as untrusted content.
- **FR-023 / M365-023**: Access while the employee is signed out (for example a scheduled briefing) MUST require a separate, revocable employee opt-in. Without it, TeamBeats MUST NOT hold mail or calendar access beyond the employee's session.

### Ports
- **FR-024 / M365-024**: Product modules MUST use canonical mailbox, calendar, free and busy and room ports in the `Integrations` module; Microsoft Graph types MUST stay inside the Microsoft 365 adapter, so that a Google Workspace adapter can be added later (INT-004, constitution XVII).

## 6. Business and validation rules
- Default for new connections: every mail and calendar feature off. Turning one on is a tenant admin decision recorded in the audit log.
- Default preview line: off (`Mail.ReadBasic`), because it keeps message text out of TeamBeats.
- Default access lifetime: the employee's TeamBeats session. Delegated tokens are kept server side for the session and deleted at sign-out, session revocation or membership disable, unless the employee opted in under FR-023.
- Default list sizes: 5 messages; 7 days of events. The tenant can lower the maximums.
- Free and busy covers today only on profile cards; pickers can ask for a chosen day.
- Rooms list refresh: daily, and on admin "refresh now".
- Mail and calendar content is never indexed by search (spec 012), never used for analytics, and never sent to an AI provider unless FR-022 applies.

## 7. Tenant and organization scope
- Tenant-owned data: connection feature settings, delegated token records per tenant membership, opt-in records, room list cache.
- Organization scoping: the tenant admin MAY limit features to org subtrees or groups (for example rooms only for head office). An employee outside the scope does not see the components.
- Cross-tenant denial cases: a token or cache entry of tenant A used for a request in tenant B; a component on tenant B's host calling with tenant A's session; a room list of tenant A shown in tenant B; free and busy of a user in another TeamBeats tenant.
- Sibling, parent and child boundary cases: free and busy of a colleague in a sibling org unit is shown only if spec 004 lets the viewer see that profile; a child org admin cannot turn on mail for the whole tenant; a feature scoped to a parent subtree applies to its children unless excluded.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Connection feature settings | Product | Internal | Life of tenant | No |
| Delegated token record (encrypted, per membership) | Product (key in secret manager) | Restricted | Session, or until opt-out or membership end when opted in | Yes |
| Offline access opt-in | Product | Internal | Life of membership plus 2 years | Yes |
| Mail list and counts | Microsoft 365 | Confidential | Not stored; in-memory cache up to 2 minutes, keyed by tenant and membership | Yes |
| Calendar events, free and busy | Microsoft 365 | Confidential | Not stored; in-memory cache up to 5 minutes, keyed by tenant and membership | Yes |
| Room list cache | Microsoft 365 | Internal | Replaced at each refresh | No |
| Room booking | Microsoft 365 (employee's calendar) | Internal | Owned by Microsoft 365 | Yes |

Sync direction is read only from Microsoft 365, except room booking, which writes one event to the employee's own calendar. Field ownership stays with Microsoft 365; TeamBeats never edits mail.

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `integration.microsoft365` (all features of this spec, SharePoint in 011) | off (add-on) | on | on |

No new capability or permission key. Admin work uses `integration.manage`. The assistant and agent use of FR-022 also needs the AI keys of specs 013 and 020.

## 10. UX, localization and accessibility
- States for both components: loading skeleton, empty ("No new mail", "Nothing on your calendar"), not connected (with Connect Outlook), admin setup needed, no mailbox, turned off, degraded with data age, conditional access, no permission. All come from the shared state definitions of spec 008 FR-006.
- Both components are lists for screen readers, with unread, importance, tentative and online markers announced in text.
- Join and open actions say where they go ("opens Outlook in a new tab").
- Arabic and English in R2 as in spec 007; dates and times follow the tenant digit setting and the employee's time zone.
- Connect Outlook uses the Microsoft sign-in page; TeamBeats never asks for a Microsoft password.

## 11. AI behavior and data policy
- Calendar data for the assistant (R2) is Confidential, goes only to providers allowed for Confidential data (spec 013), and is never kept in prompts, logs or evaluation sets beyond the request (constitution IV and spec 034 rules).
- Mail summaries (R3) are off by default at tenant and employee level.
- No-evidence behavior: if Graph is unavailable, the assistant says it cannot reach the calendar now; it does not guess.
- Injected instructions in subjects, previews or locations are data, not instructions (spec 027).

## 12. Integrations and events
- Publishes: `OutlookConnected`, `OutlookDisconnected`, `RoomBooked`. Feature, consent and health changes use the shared connection events of spec 006: `ConnectionFeatureChanged`, `ConnectionConsentRevoked`, `ConnectorUnavailable`, `ConnectorRestored`.
- Consumes: `ConnectionFeatureChanged` and `ConnectionConsentRevoked` (spec 006, to update placements and drop tokens), `UserDeprovisioned` and `SessionRevoked` (spec 003, to delete tokens), `EntitlementsChanged` (spec 005), `LeaveApproved` (spec 022, for the merged view), `EventRegistrationChanged` (spec 009).
- External system: Microsoft Graph, through the Microsoft 365 adapter of the `Integrations` module and the connector framework (spec 017): retry with backoff on 429 and 5xx, idempotent booking by a client request ID, status and audit.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| Feature, option and consent changes | audit | recorded by the shared connection audit events of spec 006 |
| integration.outlook_connected / disconnected | audit | membership ref, features |
| integration.offline_access_opted_in / out | audit | membership ref, feature |
| integration.room_booked | audit | membership ref, room ID, slot (no subject) |
| integration.graph_call | telemetry | feature, status, latency, throttled (no content, no subjects, no addresses) |
| m365_component.viewed / action | analytics | component key, action (open, join, new message), pseudonymous user (spec 021) |

## 14. Security and abuse cases
- Over-privileged app registration: FR-003 refuses application mail and calendar permissions and warns on them.
- Token theft: delegated tokens never reach the browser (spec 003 FR-006), are encrypted at rest with a key in the secret manager, are never logged, and are deleted at sign-out unless opted in.
- Reading another employee's mail by changing an ID in a request: there is no user ID in the mail and calendar APIs; they always act as the session's membership.
- Phishing through previews: previews are plain text, links are not clickable, sender display names are shown with the address on focus.
- Free and busy as a tracking tool: status only, today only on cards, only for profiles the viewer can already see.
- Room booking abuse: the room mailbox's own policies and Microsoft 365 limits apply; TeamBeats adds a rate limit per employee.
- Cache leak across tenants: cache keys include tenant and membership (spec 032 FR-065).

## 15. Non-functional requirements
- My mail and My calendar render from cache under 200 ms p95 and from Graph under 1.5 s p95, without delaying other placements.
- Graph calls per employee per minute are bounded (default 6 for refreshes) to stay inside Microsoft throttling limits at 50,000 employees.
- Connector-down tests (constitution XXVIII): consent revoked, throttled, Graph unavailable, partial failure (mail works, calendar fails), duplicate booking request.

## 16. Success criteria
- **SC-001**: In the R2 pilot, 60% of weekly active employees with a mailbox use My mail or My calendar from the home page at least once a week.
- **SC-002**: A security review confirms TeamBeats holds no application mail or calendar permission and no stored mail or event content.
- **SC-003**: With Microsoft Graph unavailable, the home page loads within its normal budget and shows the degraded state in both components.
- **SC-004**: An employee books a free room in under 60 seconds in usability tests.

## 17. Open questions
- [NEEDS CLARIFICATION: should mail and calendar come in R1 instead of R2, since Entra ID sign-in is already in R1?] Default: R2, together with the connector framework (spec 017) and SharePoint (spec 011).
- [NEEDS CLARIFICATION: should Workplace include Microsoft 365 mail and calendar without the add-on?] Default: add-on, as `integration.microsoft365` is today.
- [NEEDS CLARIFICATION: should Teams presence (available, in a call) be shown on profile cards?] Default: R3, needs `Presence.Read.All`, opt-in by tenant.
- [NEEDS CLARIFICATION: is on-premises Exchange Server needed for any target customer?] Default: not planned; hybrid through Graph only.
- [NEEDS CLARIFICATION: should employees see their OneDrive recent files on the home page?] Default: R3, as a further feature of the same connection.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| INT-001 | FR-001, FR-024 |
| INT-002 | FR-001 to FR-024 |
| INT-009 | FR-011, section 12 |
| UC-10 (mail and calendar part) | US1 to US5 |
| M365-001 to M365-005 | FR-001 to FR-005, US1 |
| M365-006 to M365-013 | FR-006 to FR-013, US2 |
| M365-014 to M365-017 | FR-014 to FR-017, US3 |
| M365-018 | FR-018, US4 |
| M365-019 | FR-019, US1 |
| M365-020, M365-021 | FR-020, FR-021, US5 |
| M365-022, M365-023 | FR-022, FR-023, US6 |
| M365-024 | FR-024 |

## Review checklist
- [x] No implementation details (languages, frameworks, table names)
- [x] Every FR is testable and traces to a requirement ID or is marked new
- [x] Cross-tenant and org boundary cases are listed
- [x] Entitlement behavior is defined
- [x] Audit events are defined
- [ ] No open [NEEDS CLARIFICATION] items that change scope (release and package defaults to confirm)
