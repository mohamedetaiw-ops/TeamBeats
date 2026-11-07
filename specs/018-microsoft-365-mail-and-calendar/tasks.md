# Tasks: Microsoft 365 Mail, Calendar and Rooms

Depends on 003 (Entra ID sign-in), 006 (Microsoft 365 connection), 005 (entitlements). R1 covers User Stories 1 to 4 and the calendar part of User Story 6; Rooms (User Story 5) are R2.

## Phase 1: Foundations
- [x] T001 Migration: `connection_feature.options`, `delegated_token` (RLS)
- [x] T002 Canonical ports `IMailbox`, `ICalendar`, `IAvailability` and `M365State` in `Integrations.Contracts`
- [x] T003 Token store with AES-GCM encryption bound to tenant, membership and session; key from platform configuration
- [x] T004 Fake Microsoft identity platform and Graph support for delegated tokens, mail, calendar view, schedule and faults

## Phase 2: User Story 1, admin turns on mail and calendar (P1)
- [x] T005 [P] [US1] Tests: features off by default with plain-word permissions; preview line swaps the scope; no application mail or calendar permission; turning a feature off drops tokens it alone needed; not offered without `integration.microsoft365`
- [x] T006 [US1] Feature options on the connection feature API, scope calculation per feature set, clean-up on `ConnectionFeatureChanged`

## Phase 3: Connect and disconnect Outlook (P1, spec 003 FR-016)
- [x] T007 [P] Tests: connect with only the needed scopes and `login_hint`; consent missing gives `admin_setup` with no employee prompt; conditional access state; disconnect deletes the token and is audited; sign-out, session revocation and membership disable delete tokens; token never in responses or logs; a token row cannot be used from another tenant or session
- [x] T008 Connect, callback, status and disconnect endpoints; `OutlookConnected` and `OutlookDisconnected`; handlers for `SessionRevoked`, `UserDeprovisioned` and `ConnectionDeleted`

## Phase 4: User Story 2, My mail (P1)
- [x] T009 [P] [US2] Tests: unread count and latest messages with markers, Focused or All, top limits, preview as plain text only when on, web links, no mailbox, degraded with data age on 429 and 5xx, 2 minute cache and refresh
- [x] T010 [US2] `IMailbox` Graph adapter and `GET /api/v1/me/mail`

## Phase 5: User Story 3, My calendar (P1)
- [x] T011 [P] [US3] Tests: range 1 to 14 days, series expanded, all-day first, cancelled and declined hidden, free and tentative marked, join link, private subjects hidden by option, degraded state
- [x] T012 [US3] `ICalendar` Graph adapter and `GET /api/v1/me/calendar`
- [ ] T013 [US3] Merge registered events (009) and leave and holidays (022) once those modules exist

## Phase 6: User Story 4, colleague free and busy (P2)
- [x] T014 [P] [US4] Tests: status text only; nothing when the viewer cannot see the profile or the colleague does not share; cross-tenant user is not found
- [x] T015 [US4] `IAvailability` Graph adapter and `GET /api/v1/people/{userId}/availability`

## Phase 7: Experience (with specs 008, 032, 033)
- [x] T016 My mail and My calendar components with every state of section 10, the unread badge, and the Outlook section in personal settings, in English and Arabic, RTL

## Phase 8: User Story 6, calendar for the assistant (P2)
- [ ] T017 Calendar read tool under spec 027 with untrusted-content handling, once the tool registry exists; signed-out opt-in (FR-023) with the briefing agent

## Final phase
- [ ] T018 Telemetry `integration.graph_call` without content; rate limit per employee; OpenAPI; R1 implementation notes; spec evidence
