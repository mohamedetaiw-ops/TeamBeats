# TeamBeats store app shell

The iOS and Android store apps of spec 030 R3 (MOB-020 to MOB-024), built as a Capacitor shell around each tenant's PWA. ADR-0023 records why and how. The shell has no product pages: every page is the web app.

## What is here

- `src/rules.ts`: the host, managed configuration and lock rules, as plain functions. `npm test` runs `test/rules.test.ts` with the Node test runner; the web CI job runs the same file.
- `src/main.ts` and `www/index.html`: the start. It reads managed configuration, finds the workspace, asks `GET /api/v1/mobile/shell`, configures the navigation guard and opens the workspace. Without a workspace it opens the platform start page `/app-start` of the web app.
- `native/android/ShellGuardPlugin.kt` and `native/ios/ShellGuardPlugin.swift`: the navigation guard (MOB-021). Copy them into the generated projects and register the plugin (`registerPlugin(ShellGuardPlugin::class.java)` in `MainActivity`; on iOS the plugin registers itself through `CAPBridgedPlugin`).
- `capacitor.config.ts`: app id and name from `TEAMBEATS_APP_ID` and `TEAMBEATS_APP_NAME`, so one TeamBeats app and a branded app per customer build from the same code.

The web app applies the app lock and registers native push when it runs in the shell (`src/Web/lib/mobile/shell.ts`); it imports no Capacitor package.

## Build

On a machine with Node 22, Android Studio and Xcode:

```sh
npm ci
npm run build
npx cap add android
npx cap add ios
npx cap sync
```

Then add the Firebase configuration files of the store project (`google-services.json`, `GoogleService-Info.plist`), the APNs key in the Firebase project, and the signing keys. None of these belong in the repository. Set the start page in `www/index.html` (`data-start-url`) to the platform host of the deployment.

## Managed configuration keys

| Key | Type | Effect |
|---|---|---|
| `workspaceHost` | string | The workspace to open, for example `contoso.teambeats.app` |
| `lockWorkspace` | boolean | Opens only that workspace and never the start page |
| `appLock` | `off`, `optional` or `required` | Applied only when stricter than the tenant policy |
| `appLockMinutes` | 0 to 60 | Applied only when shorter than the tenant policy |

## Not done here

Store publishing waits for the store accounts and Mohamed's decision on one TeamBeats app or a branded app per customer. Native builds run on device farms, not in this repository's CI.
