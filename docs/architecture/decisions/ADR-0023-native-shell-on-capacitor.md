# ADR-0023: Native store apps as a Capacitor shell around the PWA

| Field | Value |
|---|---|
| Status | Accepted (2026-11-26, under Mohamed's standing approval to proceed) |
| Date | 2026-11-26 |
| Owners | Mohamed |
| Related specs | 030 (MOB-020 to MOB-025, User Story 6), 015 (push channel), 026 (security policy), 003 (sign-in); constitution XXII, XXX, XXXI |

## Context and constraints
Spec 030 settled "PWA first, native shell second". R3 adds store apps for iOS and Android because customers with mobile device management (MDM) can only push, configure and lock apps from the stores, iOS Web Push needs the app to be installed from Safari first, and some customers require a biometric lock. MOB-020 says the store apps wrap the same PWA without separate features, so no product page may be written twice. The shell must open only the person's workspace and its sign-in hosts (MOB-021), receive push through the spec 015 channel (MOB-022), lock with the device biometrics or passcode by the tenant policy (MOB-023), and read managed configuration from MDM (MOB-024). Constitution XXII allows only OSI licensed dependencies.

## Options considered
1. **Native apps in Swift and Kotlin.** Best platform fit, but every page is built three times and MOB-020 is broken by design. Rejected.
2. **React Native or Flutter with a web view for most pages.** Brings a second UI framework, a second design system and a second build for the few native screens. Rejected.
3. **Trusted Web Activity on Android and a hand-written WKWebView app on iOS.** Thin, but two unrelated code bases, no shared plugin model, and TWA cannot run the app lock or read managed configuration. Rejected.
4. **Capacitor (MIT) shell loading the tenant's PWA (chosen).** One small shell for both platforms with maintained, MIT licensed plugins for push, app state, secure preferences, biometrics and managed configuration, and a plugin hook to decide every navigation.

## Decision
Option 4. The shell lives in `mobile/shell` and has no product pages of its own.
- **Start.** The shell reads managed configuration (`workspaceHost`, `lockWorkspace`, `appLock`, `appLockMinutes`). With a workspace host it asks that host for `GET /api/v1/mobile/shell` through the native HTTP client; without one it opens the platform's `/app-start` page of the web app, where the person types their workspace address. The shell refuses a workspace whose answer says `available: false` (the package has no `mobile.native_shell`) and says so on the start page.
- **Navigation.** A small shell plugin (`ShellGuard`, Kotlin and Swift, in `mobile/shell/native`) answers the Capacitor `shouldOverrideLoad` hook: https addresses on the workspace host and on the sign-in hosts from the shell answer load in place, `/app-start` on the platform host loads in place, and everything else opens in the system browser. With `lockWorkspace` set, the start page and any other workspace are refused.
- **Lock.** The PWA, when it runs in the shell, applies the lock (spec 030 T017): on return from the background after the set minutes it covers the page and asks the biometrics plugin to verify the person with the device biometrics or passcode. The stricter of the tenant policy and the managed configuration wins. Unlocking never signs in, never extends a session and is never sent to the server; the spec 026 session rules still end the session.
- **Push.** The PWA, when it runs in the shell and `nativeAvailable` is true, asks the push plugin for permission and the messaging token and registers it with `POST /api/v1/me/push/native`; turning push off or signing out calls `native:remove`. The server sends through FCM HTTP v1 (`FcmPushClient`, Integrations), which reaches iOS through APNs.
- **Dependencies** (all MIT, pinned in `mobile/shell/package.json`): `@capacitor/core`, `@capacitor/cli`, `@capacitor/android`, `@capacitor/ios`, `@capacitor/app`, `@capacitor/preferences`, `@capacitor/push-notifications`, `@aparajita/capacitor-biometric-auth`, `@capawesome/capacitor-managed-configurations`. The web app imports none of them: it feature-detects `window.Capacitor` and calls the plugins by name, so the web build does not change.
- **Rules as code.** The host, configuration and lock rules are plain functions in `mobile/shell/src/rules.ts`, tested with the Node test runner in the web CI job. The native projects are generated with `npx cap add android` and `npx cap add ios` on a build machine with the platform SDKs and are not kept in the repository.

## Consequences
- Every page stays one page: a fix or feature in the web app reaches the store apps with the next deploy, with no store release.
- The store apps need the store accounts, signing keys, the Firebase project and the APNs key. Publishing waits for Mohamed's decision on one TeamBeats app or a branded app per customer; the shell supports both because the workspace comes from the person or from MDM.
- Native builds run on device farms, not in this repository's CI; the rules, the server side and the web bridge are tested here.
- A new plugin, or a change to which hosts load in place, is a change to spec 030 and this ADR.
