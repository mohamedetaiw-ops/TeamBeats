# Feature Specification: Localization and Accessibility Foundation

| Field | Value |
|---|---|
| Spec ID | 007-localization-and-accessibility |
| Area | experience |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R0 (framework), R1 (ar and en complete), R2 (fr, ur, hi complete) |
| Last updated | 2026-11-05 |
| Approved | 2026-11-04 by Mohamed |
| Requirement IDs | LOC-001, LOC-002, LOC-003, LOC-004, EXP-008, CX-ACC-001 to CX-ACC-014, CX-LOC-001 to CX-LOC-005, CX-LOC-007 to CX-LOC-010 |
| Use cases | UC-04 (localization part) |
| Depends on | 001 |

## 1. Problem and outcome

**Problem:** The target market needs Arabic and Urdu right-to-left, plus English, French and Hindi. Retrofitting RTL and string externalization after screens are built is expensive and produces broken layouts.

**Outcome:** From the first screen, all UI text is externalized, every layout works in both directions, users pick their language, content can have language variants with fallback, and every rendered page variant and every administration and editor journey (including the page builder of spec 032) meets WCAG 2.2 AA in every combination of theme, density, locale, organization and personalization.

The Composable Intranet Experience requirements ([source](../../docs/product/composable-experience-requirements.md), IDs prefixed `CX-`) add accessibility and localization rules for the experience layer; they are written into this spec.

## 2. Scope

**In scope:**
- Locale model: supported locales, tenant-enabled locales, user preference, resolution order.
- Text direction resolved per rendered locale.
- String externalization for UI, emails, notifications and validation messages.
- Locale-aware formatting of dates, times, numbers, and calendars.
- Content variant model (one item, several language versions, fallback).
- Shared design primitives that are direction-aware.
- Accessibility baseline and checks, covering employee pages, administration screens and authoring tools (page builder, theme editor, navigation editor).
- Accessibility of every variation introduced by themes, layouts, locales, targeting and personalization (specs 008, 032, 033).
- Text expansion resilience, direction-neutral layouts and font fallback by script.

**Out of scope:**
- Machine translation of content (spec 009 US5, through the AI Gateway of spec 013).
- Multilingual search ranking (spec 012), though this spec records the Arabic and Urdu risk.

## 3. Actors
Employee (chooses language), tenant admin (enables locales, sets default), content editor (creates variants), developers (use primitives).

## 4. User scenarios

### User Story 1: Language and direction (Priority: P1)

**Acceptance scenarios:**
1. **Given** tenant default `ar` and the user has no preference, **When** they open the home page, **Then** UI is Arabic, `dir="rtl"` and `lang="ar"` are set, and navigation is mirrored.
2. **When** the user switches to English, **Then** the UI is English LTR and the preference is saved to their profile.
3. **Given** a user preference of `fr` that the tenant has not enabled, **Then** the organization default is used, or the tenant default when no organization sets one.
4. Resolution order: user preference (if enabled) > organization default (nearest org unit that sets one, spec 033) > tenant default > `en`. *(CX-LOC-003)*
5. **Given** Arabic and then Urdu after English, **Then** RTL layout, correct numerals and URLs and keyboard order hold in every reference template (source scenario S05). *(CX-LOC-002)*

### User Story 2: Mixed-direction content (Priority: P1)

**Acceptance scenarios:**
1. **Given** an Arabic paragraph containing an English product name and a number, **When** rendered, **Then** text order is correct (bidi isolation applied to user-generated inline content).
2. **Given** an English page inside an Arabic UI, **Then** the content block is rendered LTR within the RTL shell.
3. **Given** numerals, email addresses, URLs and identifiers inside Arabic or Urdu text, **Then** they render without unexpected reordering through directional isolation. *(CX-LOC-004)*

### User Story 3: Content variants and fallback (Priority: P2)

**Acceptance scenarios:**
1. **Given** a page with `ar` and `en` variants, **When** an `ar` user opens it, **Then** they see `ar`.
2. **Given** a page with only `en`, **When** an `ar` user opens it, **Then** they see `en` with a "not available in your language" notice, if the item's fallback is allowed. *(LOC-004)*
3. **Given** the editor marks a variant as required for publishing, **When** it is missing, **Then** publishing is blocked (UC-04 exception).

### User Story 4: Formatting (Priority: P2)

**Acceptance scenarios:**
1. Dates and numbers follow the locale. (Decided 2026-11-04: Western digits and Gregorian calendar by default, a tenant setting to switch to Arabic-Indic digits, Hijri as an optional secondary date in a later release.)
2. Times show in the user's time zone, falling back to the tenant time zone.

### User Story 5: Accessibility (Priority: P1)

**Acceptance scenarios:**
1. All primary journeys are keyboard operable with visible focus.
2. Automated accessibility checks pass with zero critical violations on every primary screen in CI.
3. Status and validation never rely on color alone. *(Foundation section 10, item 10)*
4. Screen reader announces language changes correctly through `lang` attributes.
5. **Given** the page builder, theme editor and navigation editor, **Then** they meet the same AA bar as employee pages, with automated and manual audits of complete page variants and editor journeys. *(CX-ACC-001)*
6. **Given** any essential interaction, including placing, moving and configuring components, **Then** it completes with keyboard alone, and every drag action has a single-pointer, non-drag alternative. *(CX-ACC-002, CX-ACC-003)*
7. **Given** a dialog, editor panel or asynchronous update, **Then** focus is visible, never obscured by sticky shell elements, and returns to a logical place afterwards. *(CX-ACC-004)*
8. **Given** any theme from spec 033, **Then** normal text has at least 4.5:1 contrast, large text 3:1 and essential UI cues 3:1, checked automatically on tokens and on sample pages. *(CX-ACC-005)*
9. **Given** text zoom at 200%, **Then** no information or function is lost; **Given** a 320 CSS pixel wide viewport, **Then** content reflows without two-dimensional scrolling except allowed content such as data tables and maps. *(CX-ACC-006, CX-ACC-007)*
10. **Given** a screen reader, **Then** regions, headings, controls, statuses, names and descriptions are programmatically available on every reference template. *(CX-ACC-008)*
11. **Given** reduced motion set by the user or the device, **Then** nonessential animation and smooth scrolling stop without loss of function. *(CX-ACC-010)*
12. **Given** an image asset or a component that shows media, **Then** an accessible name and alternative text are required where applicable and publishing flags missing ones. *(CX-ACC-011)*
13. **Given** a personalized layout, density or theme mode, **Then** the result is still AA-eligible in the test suite. *(CX-ACC-013)*
14. **Given** an authentication, validation, timeout or permission error, **Then** the message explains recovery in plain language without relying on color or position. *(CX-ACC-014)*

## 5. Functional requirements

- **FR-001**: Supported locales MUST be `ar`, `en`, `fr`, `ur`, `hi`. Tenants enable a subset. *(LOC-001)*
- **FR-002**: `ar` and `ur` MUST render right-to-left; others left-to-right. Direction is resolved per rendered locale, never assumed from tenant default. *(LOC-002)*
- **FR-003**: No user-facing literal strings in code. All strings come from resource catalogues with keys. *(LOC-003)*
- **FR-004**: Missing translations follow the fallback of FR-011 and are reported by a CI check that fails on missing keys for locales marked "launch-complete".
- **FR-005**: Content items MUST support language variants, a fallback rule and per-variant status. *(LOC-004)*
- **FR-006**: User-generated content MUST stay in its original language unless translation is requested.
- **FR-007**: Shared UI primitives MUST use logical properties (start/end, not left/right) and mirror directional icons.
- **FR-008**: Primary employee and admin journeys MUST meet WCAG 2.2 AA. *(EXP-008)*
- **FR-009**: Emails and notifications MUST be rendered in the recipient's resolved locale.
- **FR-010**: Fonts MUST cover Arabic, Urdu (Nastaliq or Naskh, see open question), Devanagari and Latin, and be self-hostable for on-prem. Theme font stacks (spec 033) MUST declare readable fallback faces per script so that no supported character renders as a missing glyph. *(CX-LOC-009)*
- **FR-011**: Language MUST resolve in the order user preference, organization default, tenant default, `en`. A missing translation MUST fall back deterministically in that order and the missing key MUST be logged in every environment. *(CX-LOC-003)*
- **FR-012**: Shell and editor MUST load every supported locale through resource catalogues. *(CX-LOC-001)*
- **FR-013**: Numerals, emails, URLs and identifiers in mixed-direction text MUST be isolated so they do not reorder. *(CX-LOC-004)*
- **FR-014**: Dates, times, numerals, currency and relative labels MUST format according to the locale and the tenant policy, with snapshot tests for every supported locale. *(CX-LOC-005)*
- **FR-015**: Regions and controls MUST absorb translated string expansion without clipping; pseudo-localized long strings MUST pass responsive tests on every reference template. *(CX-LOC-008)*
- **FR-016**: Layouts, the layout engine and all components MUST use logical positioning only; switching direction MUST NOT break hierarchy or action placement. *(CX-LOC-010)*
- **FR-017**: Admin tools SHOULD show translation status for pages, template labels, navigation and configurable text, and block publishing when the tenant requires a locale that is incomplete (spec 032 FR-047). *(CX-LOC-007)*
- **FR-018**: User-facing pages and all administration and editor interfaces MUST meet WCAG 2.2 AA, verified by automated checks in CI and by manual audits of complete page variants and editor journeys. *(CX-ACC-001)*
- **FR-019**: All essential interactions MUST work with keyboard alone, and every drag action MUST have a single-pointer non-drag alternative. *(CX-ACC-002, CX-ACC-003)*
- **FR-020**: Focus MUST be visible, logical, not obscured, and restored after dialogs, panels and asynchronous updates. *(CX-ACC-004)*
- **FR-021**: Contrast MUST be at least 4.5:1 for normal text and 3:1 for large text and essential graphical cues, except WCAG-defined exceptions, enforced on design tokens (spec 033 FR-009) and checked on sample pages. *(CX-ACC-005)*
- **FR-022**: Text MUST scale to 200% without loss, and content MUST reflow at 320 CSS pixels without two-dimensional scrolling except allowed content. *(CX-ACC-006, CX-ACC-007)*
- **FR-023**: Regions, headings, controls, statuses, names and descriptions MUST be programmatically available. *(CX-ACC-008)*
- **FR-024**: States, errors and status MUST never rely on color alone. *(CX-ACC-009)*
- **FR-025**: Animations and scroll behaviors MUST honor reduced-motion preferences. *(CX-ACC-010)*
- **FR-026**: Asset and component contracts MUST carry accessible names and alternative descriptions where applicable; publishing MUST flag missing required descriptions. *(CX-ACC-011)*
- **FR-027**: The page builder MUST surface accessibility errors at page, region and placement level with remediation guidance (spec 032 FR-046). *(CX-ACC-012)*
- **FR-028**: Every change of density, theme mode or region order MUST keep the page AA-eligible; the test matrix MUST include personalized variants. *(CX-ACC-013)*
- **FR-029**: Authentication, validation, timeout and permission errors MUST give accessible, non-technical recovery guidance. *(CX-ACC-014)*

## 6. Business and validation rules
- A locale is "launch-complete" when every UI key has a reviewed translation and native-speaker sign-off (SC-003). FR-004 fails CI on missing keys only for launch-complete locales.
- The tenant default locale must be one of the tenant's enabled locales (spec 001).
- Pseudo-localization is available in Development and Test only.

## 7. Tenant scope
Tenant-enabled locales and default are tenant settings (spec 001). An organization default locale, chosen from the tenant-enabled locales, is an organization experience setting (spec 033). User preference is per tenant membership.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Personal data? |
|---|---|---|---|
| Resource catalogues (UI strings) | Product (source control) | Public | No |
| User locale preference | Product | Internal | Yes |
| Content variants | Owning content module (009, 011, 025) | Same as the item | Per item |

## 9. Package entitlements
All five locales and accessibility are available in every package. No capability key.

## 10. UX
- Every screen follows the TeamBeats Design System (spec 036, `docs/design-system`), whose accessibility and content guidelines implement this spec; its design suite runs axe, reflow and target size checks in all three designs in Arabic and English.
- Language is chosen in one place: Quick preferences in the shell header and the same control on the personal Preferences page (spec 036 DLS-047).
- Pseudo-localization locale for testing (long strings, accented characters, forced RTL).

## 11. AI behavior
Not applicable here. AI answers follow the user's resolved locale (specs 014 and 020).

## 12. Integrations and events
Publishes `UserLocaleChanged` so notifications (spec 015) and agents pick up the new language.

## 13. Audit
Tenant locale changes are audited by spec 001 (`tenant.settings_changed`). A user's own language preference is not audited.

## 14. Security and abuse cases
- Bidirectional control characters (for example U+202E) in names, file names and titles can disguise text ("exe.pdf" shown as "fdp.exe"). User-entered names are rendered with bidi isolation, and file names show their real extension separately.
- Mixed-script look-alike names used to impersonate people or sites: display names show the account's verified email or unit next to the name in sensitive places (approvals, sharing dialogs).

## 15. Non-functional requirements
- Locale switching without full reload where the framework allows.
- Font payload for the initial page under the LCP budget (2.5 s target, foundation section 14).

## 16. Success criteria
- **SC-001**: 100% of UI keys translated for launch-complete locales.
- **SC-002**: Zero RTL visual regressions on primary screens in CI.
- **SC-003**: Native-speaker review sign-off for each launch locale before its release.
- **SC-004**: Keyboard-only, single-pointer and screen reader runs complete page construction and placement ordering in the page builder (source scenario S09).
- **SC-005**: For each sample page, the risk-based pairwise matrix of scope, role, theme mode, locale (including RTL), viewport, personalization state and publication version passes AA checks, with full coverage for tenant isolation and critical authorization paths (source section 8.2).

## 17. Open questions
1. RESOLVED 2026-11-04 (Mohamed): `ar` and `en` are launch-complete in R1. `fr`, `ur` and `hi` are launch-complete in R2. The framework, RTL support and resource catalogues for all five exist from R0.
2. Decided 2026-11-04: Nastaliq for Urdu content, with a font performance test before R2.
3. Digits and calendar for Arabic (see US4).
4. Risk to record for the search spec: PostgreSQL full-text search has limited stemming support for Arabic morphology and none for Urdu. A search quality spike is needed before committing to the search approach.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| LOC-001 | FR-001 |
| LOC-002 | FR-002, US1, US2 |
| LOC-003 | FR-003, FR-004 |
| LOC-004 | FR-005, US3 |
| EXP-008 | FR-008, US5 |
| CX-LOC-001 | FR-001, FR-012 |
| CX-LOC-002 | FR-002, US1 |
| CX-LOC-003 | FR-011, US1 |
| CX-LOC-004 | FR-013, US2 |
| CX-LOC-005 | FR-014, US4 |
| CX-LOC-007 | FR-017 |
| CX-LOC-008 | FR-015 |
| CX-LOC-009 | FR-010 |
| CX-LOC-010 | FR-007, FR-016 |
| CX-ACC-001 | FR-018, US5 |
| CX-ACC-002 | FR-019, US5 |
| CX-ACC-003 | FR-019, US5 |
| CX-ACC-004 | FR-020, US5 |
| CX-ACC-005 | FR-021, US5 |
| CX-ACC-006 | FR-022, US5 |
| CX-ACC-007 | FR-022, US5 |
| CX-ACC-008 | FR-023, US5 |
| CX-ACC-009 | FR-024, US5 |
| CX-ACC-010 | FR-025, US5 |
| CX-ACC-011 | FR-026, US5 |
| CX-ACC-012 | FR-027 |
| CX-ACC-013 | FR-028, US5 |
| CX-ACC-014 | FR-029, US5 |
