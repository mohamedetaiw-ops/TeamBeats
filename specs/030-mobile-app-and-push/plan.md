# Implementation Plan: Mobile App, PWA and Push

**Spec:** `specs/030-mobile-app-and-push/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0014 (frontend), spec 008 plan (shell), spec 015 plan (channels), spec 033 plan (brand tokens), spec 026 plan (session policy)

## Summary
030 is a client capability, not a module. The web app gains a per-tenant manifest endpoint in `Experience`, a service worker built with the existing Vite toolchain, responsive layouts in the shell, and an IndexedDB cache encrypted with Web Crypto. Push is a Web Push channel adapter in `Notifications` (015) with VAPID keys from the secret manager. Kiosk devices and badge sign-in live in `Identity` (003) with device policy from 026.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I tenant isolation | Yes | Manifest per tenant host; cache keys include tenant and user; kiosk badges tenant-bound |
| III authorization | Yes | Kiosk feature block enforced in the API by a session flag checked by `IAuthorizationService` |
| Rule 6 dependencies | Yes | Web Push needs VAPID signing; the .NET `WebPush` package (MPL 2.0, OSI) is the only addition, used inside the adapter |
| Rule 11 frontend | Yes | No business rules in the service worker; it caches what the API marks cacheable |

## Key decisions
- The API marks offline-eligible responses with a cache header the service worker reads; classification decides eligibility on the server.
- Kiosk sessions are a session type with no refresh token and a server-side idle timer.
- Badge QR encodes an opaque badge ID, never the employee number.

## Tests
Playwright mobile viewport suite at 360 px in ar and en for every route; push subscription lifecycle with a fake push service; offline cache wipe on sign-out; Confidential never cached; kiosk feature block by URL; cross-tenant badge refusal; lockout.
