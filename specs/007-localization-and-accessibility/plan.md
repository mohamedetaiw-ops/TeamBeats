# Implementation Plan: Localization and Accessibility Foundation

**Spec:** `specs/007-localization-and-accessibility/spec.md` (v1.0, Approved) | **Branch:** `007-l10n-a11y` | **Date:** 2026-11-04
**Shared base:** `docs/architecture/r0-solution-structure.md`

## Summary
Set up locale resolution, direction handling, resource catalogues for all five locales (ar and en complete in R1), lint rules, design-system primitives, a content variant model contract for later content specs, and automated accessibility and RTL checks in CI.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| XXIII Localization | Yes | Literal-string lint, logical-properties lint, missing-key CI gate |
| XXIV Accessibility | Yes | axe checks in Playwright, keyboard tests, focus styles in primitives |
| XXII Dependencies | Yes | `next-intl` (MIT); fonts with OFL licence, self-hosted |

## Design
- **Resolution** (server side in Next.js middleware): user preference if tenant-enabled, else tenant default, else `en`. Sets `<html lang dir>`. Backend uses the same resolver for emails and Problem Details messages (`Accept-Language` ignored if not enabled for tenant).
- **Catalogues:** `messages/{locale}.json` (web) and `.resx`-free JSON catalogues for backend (`src/BuildingBlocks/Localization/catalogues/{locale}.json`), same key namespace. `fr`, `ur`, `hi` start as copies with a `pending` marker so missing-key checks only fail for launch-complete locales (`ar`, `en`).
- **Pseudo-locale** `en-XA` (long strings, accents) and `ar-XB` (forced RTL) available in Development/Test.
- **Formatting:** `Intl` APIs with `numberingSystem` from tenant setting (`latn` default, `arab` optional); Gregorian calendar; time zone from user, else tenant.
- **Bidi:** user-generated inline content wrapped with `<bdi>`/`dir="auto"`; content blocks get `dir` from their variant locale.
- **Primitives:** Button, Icon (with `mirrorInRtl` prop), Tree, Table, Dialog, FormField with error text and icon (never color alone), Toast with `aria-live`.
- **Fonts:** self-hosted Noto Sans Arabic, Noto Nastaliq Urdu, Noto Sans Devanagari, Inter (all OFL). Subsetting and `font-display: swap`; Nastaliq loaded only for `ur`. Font performance test before R2.
- **Content variant contract** (for spec 009 later): `ContentItem { id, defaultLocale, fallbackAllowed }`, `ContentVariant { locale, status, requiredForPublish }`.

## Tests
- Resolution matrix (user pref enabled/disabled, tenant default).
- RTL screenshot tests for admin shell and sign-in in `ar`; mixed bidi fixture.
- Lint rules fail on a planted literal string and a `margin-left`.
- Missing `ar` key fails CI; missing `hi` key does not (until R2).
- axe: zero critical violations on all R0 screens; keyboard-only walkthrough of org tree and role assignment.
