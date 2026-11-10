# End-to-end testing plan (Playwright)

Status: Proposed (test plan; follows `r0-solution-structure.md` section 7, spec 007 T013 and T014, ADR-0014)
Date: 2026-11-06

## 1. Purpose
Playwright tests prove that a person can complete the journeys the approved specs describe, through the real web app, in Arabic and English, with keyboard and screen reader support. They are the top of the test pyramid and stay small:

| Layer | Suite | Owns |
|---|---|---|
| Unit | `tests/Unit` | Domain rules |
| Integration | `tests/Integration` | Use cases and API contracts on real PostgreSQL with RLS |
| Security | `tests/Security` | Cross-tenant sweeps, org boundary, session and protocol rules for every endpoint |
| Architecture | `tests/Architecture` | Module boundaries and constitution rules |
| Acceptance | `tests/Acceptance` (Playwright) | User journeys through the browser, language and direction, accessibility, visual layout |

A rule that the API enforces is tested in the integration or security suite. A Playwright test checks that the UI shows the outcome correctly (for example the denied state), never that hiding a link is the control (constitution, CLAUDE.md rule 2).

## 2. What exists now
- `tests/Acceptance`: Playwright 1.63 with `@axe-core/playwright`, TypeScript, Chromium.
- The suite starts `src/Api.DevHost` and `next dev` with development sign-in, or reuses running servers locally.
- Test data goes through the public API as the seeded fictional platform operator. Each test creates its own tenant.
- Expected text comes from `src/Web/messages/{ar,en}.json`.
- Tests (9, all green locally and repeated twice for stability):
  - Signed-out visitor goes to sign-in, `lang` and `dir` correct, in `en` and `ar`.
  - Appointed administrator signs in and reaches the home page, in `en` and `ar`.
  - A session token from one tenant is refused on another tenant's host.
  - Admin shell overview and a section, in `en` and `ar`.
  - Language switcher changes language and direction and the choice persists.
  - Keyboard-only sign-in, opening administration and sign-out, with the session gone on the server.
  - axe on every screen above: zero critical violations.
- CI: the `e2e` job in `.github/workflows/ci.yml` runs on every pull request with PostgreSQL 18. The HTML report and traces are uploaded only when the job fails, kept 7 days.

## 3. Conventions
1. **Isolation.** Every test creates the tenants and users it needs with unique keys. No shared fixtures that tests mutate. Tests run in parallel.
2. **Two locales.** Every user-facing journey runs in `ar` and `en`. Direction (`dir`), language (`lang`) and mirrored icons are asserted, not assumed.
3. **Accessible locators.** Roles, labels and accessible names only. A missing accessible name is an app bug.
4. **Accessibility gate.** `expectAccessible` on every new screen and on dialogs once opened. Zero critical axe violations blocks the build (spec 007 US5). Serious findings are attached to the report; the gate moves to zero serious once the R1 design-system primitives land (spec 007 T012).
5. **Keyboard.** Each interactive component pattern (tree, table, dialog, menu, builder canvas) gets one keyboard-only walkthrough.
6. **Visual checks.** Screenshot comparisons (`toHaveScreenshot`) for the shell, sign-in and each page template in `ar` and `en`, light and dark (spec 007 T014, spec 033). Baselines are generated in the CI Linux image and updated only through a reviewed PR.
7. **No external systems.** Microsoft Entra ID, Microsoft Graph and OpenRouter are replaced by fakes behind the same connector adapters and AI Gateway ports (CLAUDE.md rule 4). Tests never call real tenants, mailboxes or models. The fakes and the fictional `staging-demo` scenario are described in `docs/testing/fake-systems.md`.
8. **Data.** Fictional names and `*.example` addresses only. No secrets in tests or fixtures.
9. **Traceability.** Each spec file names the spec IDs and user stories it covers in its header comment.

## 4. R1 coverage
Each row is added in the same PR as the feature, by the build thread, so a story is not done until its journey passes. "Fake" means a test double wired in the development host by configuration, never in the production artifact (ADR-0004).

| Area | Specs | Journeys (each in `ar` and `en`) | Needs |
|---|---|---|---|
| Entra ID sign-in | 003 US2 | Sign in through the identity provider, return to the page asked for; refused account; suspended tenant page; sign-out ends the server session | Fake Entra ID pages in the development host (`docs/testing/fake-systems.md`) |
| Directory sync and org mapping | 003 US6, 002 US5, 006 | Admin connects Microsoft 365, runs a sync, sees mapped users and units; failure state shown | Fake Graph directory and consent page, `staging-demo` scenario |
| Tenancy and org boundary | 001, 002, 004 | Scoped admin sees only their units; opening another unit by URL shows the denied state; cross-tenant replay refused | Seeded org tree helper |
| Entitlements | 005 | A capability outside the package shows the not-in-package state with no data | Second package fixture |
| Shell and navigation | 008 | Global shell, navigation, quick navigation, shared empty, loading and error states | none |
| Employee home | 008, 032 | Add, hide, reorder and reset personal regions; changes persist after reload | none |
| Pages and builder | 032 | Create a page from a template, compose with the keyboard only, preview by device, direction and persona, publish | none |
| Themes | 033 | Tenant and organization brands, locked tokens, light and dark modes; screenshot baselines | none |
| News, pages and approval | 009 | Draft, submit, approve, publish; critical announcement with acknowledgement | none |
| Documents and knowledge | 011 | Upload, version, controlled document review; knowledge base article | Object storage fake (file system adapter) |
| Search | 011, 009 | Search returns only content the user may see, in both languages | Index running in the dev host |
| Mail and calendar | 018 | Home shows mail and calendar, colleague free and busy; disconnected state | Fake Graph mail and calendar |
| AI assistant | 013 | Ask a question, get a cited answer; usage view; policy blocks a model | Deterministic fake model route in the AI Gateway |
| Agents | 020, 027 | Use a launch agent; every change waits for confirmation; execution record; emergency stop | Fake model route |
| AI evaluation | 034 | Evaluation dashboard; a failed release gate blocks publishing an agent version | Seeded evaluation runs |
| Services and inbox | 016, 022 US2 | Open a linked app, submit a simple form service, act on it from the task inbox; due time follows the working calendar | none |
| Policies | 025 | Policy register, attestation, policy Q&A answer with citation | Fake model route |
| Security policy | 026 | IP rule denial page, session rules, MFA check and step-up prompt | Fake OIDC step-up claim |
| Connections | 006, 017 | Allow a host, add a custom REST connection, test it, turn it on and off, define an action, read run history, delete; add a Microsoft 365 connection, read its permissions, turn a feature off and on, delete | none (only the allowlist is checked; no action runs; Microsoft is not called) |
| Notifications | 008, 016 | In-app notification appears and links to the item; email captured by a fake mail sink | Fake mail sink |
| Localization | 007 | Bidi fixtures (Arabic text with English names and numbers), numbering system per tenant, time zone fallback | none |

## 5. Rollout
| Step | When | What |
|---|---|---|
| 1 | Now (this PR) | Suite, smoke and R0 screen tests, CI job on every PR |
| 2 | With Entra sign-in (first R1 build PR) | Fake OIDC provider; sign-in tests use it. Development sign-in stays for setup only |
| 3 | With the experience layer (008, 032, 033) | Screenshot baselines, keyboard builder walkthrough, zero serious axe gate |
| 4 | When the suite passes 5 minutes | Shard the CI job; PRs run Chromium; a nightly run adds Firefox, WebKit and a mobile viewport |
| 5 | With Fly.io staging | After each staging deploy, a smoke run with `E2E_TARGET=remote` (signed-out pages, sign-in, home, admin shell). It only reads data and creates tenants with the `e2e-` prefix, which a cleanup job closes |
| 6 | Before the first design-partner tenant | Full R1 journey set green in `ar` and `en` as an R1 exit check |

## 6. Open decisions
1. **Sign-in on staging.** PR #20 (pending) runs the development host on staging with a dedicated tenant `staging-demo` and a one-click test admin account (no password). When it merges, the remote smoke run signs in with `POST /dev/signin` and `{"username":"admin"}`, or clicks "Sign in as test admin" on the sign-in page. Longer term, a staging that runs the production API needs either a dedicated Entra ID test tenant with fictional accounts or a fake OIDC provider deployed to staging only.
2. **Production build in CI.** The suite runs `next dev` because development sign-in renders only outside production builds. Once step 2 lands, switch CI to `next build` and `next start` so tests see what users get.
3. **Browsers.** Chromium only on PRs is proposed. Confirm whether Safari (WebKit) and Firefox are required for R1 pilot browsers.
4. **Definition of done.** Proposed addition for every user-facing story: one Playwright journey in `ar` and `en` with an axe check. This would be a constitution or `tasks-template.md` change and goes in its own PR if accepted.
