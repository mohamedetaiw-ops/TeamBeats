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
- **Resolution** (server side in Next.js middleware): user preference if tenant-enabled, else organization default (nearest org unit that sets one; the slot exists from R0 and is filled once spec 033 ships), else tenant default, else `en`. A missing key falls back along the same chain and is logged. Sets `<html lang dir>`. Backend uses the same resolver for emails and Problem Details messages (`Accept-Language` ignored if not enabled for tenant).
- **Catalogues:** `messages/{locale}.json` (web) and `.resx`-free JSON catalogues for backend (`src/BuildingBlocks/Localization/catalogues/{locale}.json`), same key namespace. `fr`, `ur`, `hi` start as copies with a `pending` marker so missing-key checks only fail for launch-complete locales (`ar`, `en`).
- **Pseudo-locale** `en-XA` (long strings, accents) and `ar-XB` (forced RTL) available in Development/Test.
- **Formatting:** `Intl` APIs with `numberingSystem` from tenant setting (`latn` default, `arab` optional); Gregorian calendar; time zone from user, else tenant.
- **Bidi:** user-generated inline content wrapped with `<bdi>`/`dir="auto"`; content blocks get `dir` from their variant locale.
- **Primitives:** Button, Icon (with `mirrorInRtl` prop), Tree, Table, Dialog, FormField with error text and icon (never color alone), Toast with `aria-live`.
- **Fonts:** self-hosted Noto Sans Arabic, Noto Nastaliq Urdu, Noto Sans Devanagari, Inter (all OFL). Subsetting and `font-display: swap`; Nastaliq loaded only for `ur`. Font performance test before R2.
- **Content variant contract** (for spec 009 later): `ContentItem { id, defaultLocale, fallbackAllowed }`, `ContentVariant { locale, status, requiredForPublish }`.

## Tests
- Resolution matrix (user pref enabled/disabled, organization default set/unset, tenant default).
- RTL screenshot tests for admin shell and sign-in in `ar`; mixed bidi fixture.
- Lint rules fail on a planted literal string and a `margin-left`.
- Missing `ar` key fails CI; missing `hi` key does not (until R2).
- axe: zero critical violations on all R0 screens; keyboard-only walkthrough of org tree and role assignment.

## R2: French, Urdu and Hindi launch-complete

R2 makes `fr`, `ur` and `hi` launch-complete (spec section 17, decision 1). The framework, direction handling and pending catalogues exist since R0; R2 adds the fonts, the translation gates and the translations.

- **Fonts and type (slice 1, T019).** `@fontsource/noto-nastaliq-urdu` (400, 700) and `@fontsource/noto-sans-devanagari` (400, 600), both OFL and self-hosted like the other brand fonts (ADR-0019). Their families are used only under `lang="ur"` and `lang="hi"` (tokens.css), so other locales never download them. Nastaliq needs taller lines: Urdu sets the body line height to 2 and headings to 1.7.
- **Translation gates (slice 1, T019).** Every locale that has a key keeps the English arguments: the web check reads ICU messages (argument names only, never the words inside plural or select branches) and checks rich text tags; the backend architecture test checks `{name}` placeholders in `ar`, `fr`, `ur` and `hi`. A locale becomes launch-complete by joining `launchLocales` (web), the missing-key gates (web and backend) and the language list the tenant can enable.
- **Translations (slices 2 to 4, T020 to T022).** One slice per locale: web messages and backend catalogue, then the locale joins the launch list and its e2e pass (sign-in, home and one admin screen with axe; RTL for `ur`). The Nastaliq performance check runs with slice 3: an Urdu page downloads at most two Nastaliq files and an English or Arabic page none.
- **Review (T023).** Translations are drafted, then checked by the gates above. A native-speaker review per locale is listed as open in evidence.md until a reviewer signs it off.
