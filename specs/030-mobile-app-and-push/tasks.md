# Tasks: Mobile App, PWA and Push

Depends on 003, 008, 015, 026, 033. R2.

## Phase 1: PWA base (P1)
- [x] T001 [P] [US1] Tests: manifest per tenant and brand; update prompt never during input
- [x] T002 [US1] Manifest endpoint, service worker, update flow
- [x] T003 [P] [US5] Mobile viewport e2e suite for all routes in ar and en
- [x] T004 [US5] Responsive fixes and bottom navigation

## Phase 2: User Story 2, push (P1)
- [x] T005 [P] [US2] Tests: prompt timing, decline memory, preferences, no Confidential payload, removal on sign-out and gone
- [x] T006 [US2] Web Push adapter in 015 and subscription endpoints

## Phase 3: User Story 3, offline (P1)
- [ ] T007 [P] [US3] Tests: eligible items only, limits, encryption, wipe and prune, offline banner
- [ ] T008 [US3] Offline cache and server cache marks

## Phase 4: User Story 4, kiosk (P1)
- [ ] T009 [P] [US4] Tests: enrolment, badge and PIN, lockout, inactivity sign-out, nothing left on device, feature block by URL, revocation within 1 minute, cross-tenant badge
- [ ] T010 [US4] Kiosk devices, badge sign-in and kiosk session type

## Final phase
- [ ] T011 Lighthouse checks in CI; OpenAPI; R2 implementation notes; spec evidence
