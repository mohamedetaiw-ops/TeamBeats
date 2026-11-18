# Implementation Plan: WCAG 2.2 AA Accessibility Conformance

**Spec:** `specs/038-accessibility-conformance/spec.md` (v1.0, Approved 2026-11-18) | **Date:** 2026-11-18
**Shared base:** spec 007 (accessibility baseline), spec 036 (design suite, `docs/design-system/testing.md`), `docs/testing/e2e-plan.md`

## Summary
No new module, table or endpoint. The work is a test layer, fixes in shared components and pages, one public page and two documents:

- An `a11y` Playwright project in `tests/Acceptance/a11y` with its own CI job. It holds the route inventory, the exclusions file, the matrix suite (axe on every route) and the real-page checks (keyboard, focus, targets, reflow, zoom, text spacing, motion, overlays).
- Fixes for what the suite finds, made in `components/ui` and the shared CSS first, then in feature pages.
- The recommended `jsx-a11y` rules switched on in `src/Web/eslint.config.mjs` (the plugin already ships with `eslint-config-next`, so no new dependency).
- Non-drag move controls wherever drag exists.
- `/accessibility` statement page in the web app, linked from the footer.
- `docs/accessibility/wcag-2.2-conformance.md` (the map) and `docs/accessibility/conformance-report.md` (VPAT 2.5 WCAG edition layout).

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| XXII dependencies | Yes | No new package: `@axe-core/playwright` and `eslint-plugin-jsx-a11y` are already installed |
| XXIII localization | Yes | Statement text and move announcements from resource keys in ar and en; RTL in the matrix |
| XXIV accessibility | Yes | This spec is its verification |
| XXX frontend | Yes | Statement page renders the shell footer configuration from the API; no rules in React |
| XXXI design system | Yes | Fixes go into shared parts; statement page uses the page frame and header |

## Design

### Playwright projects
`playwright.config.ts` gets two projects: `chromium` (testDir `./specs`, the functional suite as today) and `a11y` (testDir `./a11y`). The `e2e` job runs `--project=chromium`; a new `accessibility` job runs `--project=a11y` in two shards with the same services and setup.

### Route inventory (`a11y/routes.ts`)
Each entry: path, tier (`primary` or `other`), whether it is public, and an optional setup that creates the data the page needs (a record id for a detail route). The inventory test walks `src/Web/app` for `page.tsx` files and fails on a route that is not listed or not marked `skip` with a reason (development only routes, the kiosk device page, the offline fallback).

### Matrix (`a11y/matrix.spec.ts`)
One tenant per design and language (eight tenants, package `enterprise_gov`, staging home seeded). Primary pages run in both modes and both widths. Other routes take one combination each, chosen by rotating mode and width over the inventory order, so each design, mode and width covers a share of routes in both languages. Mode is set through `PUT /api/v1/me/preference-profile` and set back afterwards. Each scan waits for fonts and for network idle, then runs axe with the A and AA tags of WCAG 2.0, 2.1 and 2.2 and fails on critical or serious violations not listed in `a11y/exclusions.ts`.

### Real-page checks (`a11y/interaction.spec.ts`)
Run on the primary pages in Beats and Studio (rail and sidebar navigation), both languages, light mode:
- Skip link: first Tab stop, Enter moves focus to `#main`.
- Tab walk (first 40 stops): each stop has a non-zero outline or box-shadow ring, no `tabindex` above 0, and is not entirely covered by a fixed or sticky element at desktop and phone width (`elementsFromPoint` at the centre and corners).
- Targets: the 24 by 24 check of the design suite, applied to `main` and the shell.
- Reflow at 320 pixels, 200 percent text through the preference profile, text spacing CSS injected and clipping measured, reduced motion through `emulateMedia` and `document.getAnimations()`.
- Overlays in the component gallery: dialog, drawer, menu, popover, bottom sheet open, trap, Escape, focus return.

### Fix order
1. Shell and shared parts (they repeat on every page).
2. Primary pages.
3. Other routes, grouped by module.

### Statement page
`app/accessibility/page.tsx`, public (the proxy lets it through signed out), page frame and header, sections from `accessibility_statement.*` messages, the date of the last conformance update as a constant next to the map, and the tenant's footer accessibility link from the shell API when signed in. The footer adds the statement link when the tenant has no `accessibility` link.

## Tests
- The suite itself: inventory, matrix, real-page checks.
- A planted failure (a test page in the gallery with an unlabeled button is not added; instead the exclusions file test proves a missing reason and a past review date fail).
- Statement page: signed out and signed in, ar and en, footer link fallback.
- Move controls: page builder and arrange mode keyboard and single click moves with announcements.

## Risks
- Run time: dev server first compile of each route is slow. Mitigation: two shards, pages warmed in the order visited, `networkidle` with a cap.
- Flaky contrast on images or gradients: fixed by waiting for fonts and images, never by excluding the rule.
