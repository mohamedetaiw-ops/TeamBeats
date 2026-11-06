# Implementation Plan: Microsoft 365 Mail, Calendar and Rooms

**Spec:** `specs/018-microsoft-365-mail-and-calendar/spec.md` (v1.0, Approved for R1 mail and calendar) | **Date:** 2026-11-06
**Shared base:** `docs/architecture/r0-solution-structure.md`, ADR-0013 (ports inside modules), spec 006 plan (Microsoft 365 connection)

## Summary
Mail, Calendar and Free and busy become delegated features of the existing Microsoft 365 connection (spec 006 US5). An employee connects Outlook once per session through an authorization code flow against the TeamBeats multi-tenant application in their own Entra directory, asking only for the delegated permissions of the features that are on. The access token is kept server side, encrypted, per tenant membership and session. Product code reads mail, calendar and free and busy through canonical ports in `Integrations.Contracts`; Microsoft Graph types stay in the Microsoft 365 adapter (FR-024). Nothing from a mailbox or calendar is stored; short in-memory caches are keyed by tenant and membership.

R1 slices, in order:
1. **Backend:** feature options, delegated connect and disconnect, token store, canonical ports, the Graph adapter, `GET /api/v1/me/mail`, `GET /api/v1/me/calendar`, `GET /api/v1/people/{userId}/availability`, and token clean-up on sign-out, revocation, disable and feature off.
2. **Experience:** the My mail and My calendar home components and the unread badge (with specs 008, 032 and 033), and the Outlook section in personal settings.
3. **Assistant:** the calendar read tool, once the agent tool registry of spec 027 exists.

Rooms (US5) are R2 and not planned here.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation | Yes | Token rows are tenant-owned with RLS and keyed by membership and session; cache keys carry tenant and membership; no user ID in mail and calendar APIs |
| III authorization | Yes | Admin work needs `integration.manage`; availability is shown only for a profile the viewer may see (until the people profile module exists, an active member of the same tenant); access to a mailbox is decided by Microsoft 365 for the signed-in user |
| XVII connectors | Yes | Canonical ports, one adapter, retry with backoff on 429 and 5xx, degraded states carry data age |
| XXI entitlements | Yes | Every endpoint checks `integration.microsoft365` |
| XXV classification | Yes | Tokens Restricted and encrypted; mail and events Confidential and never stored |
| Rule 9 secrets | Yes | Tokens never reach the browser, logs or audit; the encryption key is platform configuration from the secret store |

## Delegated access (spec 003 FR-016, spec 018 FR-002, FR-004, FR-013, FR-023)
- **Connect Outlook:** `GET /api/v1/me/outlook/connect` starts an authorization code flow with PKCE at `login.microsoftonline.com/{directory}/oauth2/v2.0/authorize` for the connection's directory, with `login_hint` set to the employee's directory UPN and the scopes of the features that are on. Because the employee already signed in with Entra ID, Microsoft normally completes this without a second sign-in. State and the PKCE verifier are bound to the session with a short-lived cookie, as in the sign-in and consent flows.
- **Callback:** `GET /api/v1/integrations/outlook/callback`, routed from the platform host to the tenant host like the admin consent callback; only the user, session and browser that started the connect can finish it. It redeems the code, stores the access token encrypted with AES-GCM (from the .NET base library, no new dependency) under a platform key `Integrations:Microsoft365:TokenKey`. The associated data binds the ciphertext to tenant, membership and session, so a row copied to another tenant or session cannot be decrypted. Audit `integration.outlook_connected`, event `OutlookConnected`.
- **Lifetime:** R1 requests no `offline_access`, so there is no refresh token. When the access token expires (about an hour), the APIs answer with state `reconnect` and the web shell repeats the connect redirect with `prompt=none`, which is silent while the Entra session lasts. The signed-out opt-in of FR-023 (with `offline_access`) comes with the briefing agent.
- **Clean-up:** tokens are deleted at sign-out (`SessionEnded`), session revocation (`SessionRevoked`), membership disable (`UserDeprovisioned`), disconnect (`integration.outlook_disconnected`, `OutlookDisconnected`), connection delete, and when a feature is turned off, an option changes or the connection is disabled and no remaining feature needs the token's scopes (in the same transaction as the change).
- **Consent missing:** when Microsoft answers `consent_required` or `interaction_required` for consent, the state is `admin_setup`, never another consent prompt for the employee (FR-004). Conditional access failures map to `conditional_access` with a link to Outlook (FR-012).

## Data model (schema `integrations`)
- `connection_feature` gains `options jsonb` (Mail: `previewLine`, maximum messages, default 10; Calendar: maximum days, default 14). A placement asks for its own count and range within those maximums. Preview line on swaps `Mail.ReadBasic` for `Mail.Read` in the requested scopes.
- `delegated_token`: `tenant_id`, `membership_user_id`, `session_id`, `connection_id`, `scopes text[]`, `ciphertext bytea`, `nonce bytea`, `expires_at`, `created_at`. Primary key `(tenant_id, membership_user_id, session_id)`. RLS.
- No table for mail, events or free and busy.

## Ports (`Integrations.Contracts`, FR-024)
- `IMailbox.GetInboxAsync(MailQuery)` returns `MailSummary { UnreadCount, Messages[] { Id, Sender, SenderAddress, Subject, ReceivedAt, IsRead, Importance, HasAttachments, Preview?, WebLink } }`.
- `ICalendar.GetEventsAsync(from, to)` returns occurrences with `Start`, `End`, `IsAllDay`, `ShowAs`, `IsPrivate`, `IsCancelled`, `Response`, `Location`, `OnlineJoinUrl?`, `WebLink`.
- `IAvailability.GetAsync(userPrincipalNames, day)` returns status blocks only (free, busy, tentative, out of office, working elsewhere).
- Each result carries a `M365State`: `ok`, `not_connected`, `reconnect`, `admin_setup`, `no_mailbox`, `turned_off`, `conditional_access`, `degraded` (with the age of the last data), `not_in_plan`.
- The ports act as the session's membership only; they take no user ID for mail and calendar.

## API (v1)
- `GET /api/v1/me/mail?view=focused|all&top=5` and `GET /api/v1/me/calendar?days=7`: state plus data; the server applies FR-014 rules (expand series with `calendarView`, hide cancelled and declined, mark free and tentative, hide private subjects when the placement asks).
- `GET /api/v1/people/{userId}/availability`: today's status text for a colleague whose profile the viewer may see; nothing when the colleague does not share free and busy.
- `GET /api/v1/me/outlook`, `GET /api/v1/me/outlook/connect`, `DELETE /api/v1/me/outlook`, and the callback `GET /api/v1/integrations/outlook/callback`.
- Admin options on `PUT /api/v1/integrations/connections/{id}/features/{feature}/options`.

## Caching and limits
- Mail lists and counts up to 2 minutes, events and availability up to 5 minutes, in memory, keyed `(tenant_id, user_id, session_id, feature, query)`; refresh on demand bypasses the cache but still counts against the per-employee limit (default 6 Graph refreshes per minute).
- Graph 429 and 5xx: retry with backoff as `Retry-After` asks, then return `degraded` with the last data of this session and its age.

## Merged calendar (FR-017)
Registered events (spec 009) and leave and holidays (spec 022) are merged through their modules' contracts when those modules exist. Until then the calendar API returns Outlook items only and the gap is listed in the R1 implementation notes.

## Tests
- Fake Microsoft identity platform and Graph (`FakeMicrosoftGraph`) with delegated tokens, `me/messages`, `me/mailFolders/inbox`, `me/calendarView`, `me/calendar/getSchedule`, and fault injection for 429, 5xx, consent missing, conditional access and no mailbox.
- Per user story: US1 scenarios 1 to 7, US2 scenarios 1 to 9 (backend part), US3 scenarios 1 to 5 and 8, US4 scenarios 1 to 3.
- Tenant boundaries: a token row of tenant A unusable in tenant B (RLS and associated data), a session of tenant A on tenant B's host, availability of a user in another tenant, cache keys.
- Token hygiene: never in responses, logs or audit; deleted on each clean-up trigger.
- Entitlement: every endpoint refuses without `integration.microsoft365`.
