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
| Requirement IDs | LOC-001, LOC-002, LOC-003, LOC-004, EXP-008 |
| Use cases | UC-04 (localization part) |
| Depends on | 001 |

## 1. Problem and outcome

**Problem:** The target market needs Arabic and Urdu right-to-left, plus English, French and Hindi. Retrofitting RTL and string externalization after screens are built is expensive and produces broken layouts.

**Outcome:** From the first screen, all UI text is externalized, every layout works in both directions, users pick their language, content can have language variants with fallback, and primary journeys meet WCAG 2.2 AA.

## 2. Scope

**In scope:**
- Locale model: supported locales, tenant-enabled locales, user preference, resolution order.
- Text direction resolved per rendered locale.
- String externalization for UI, emails, notifications and validation messages.
- Locale-aware formatting of dates, times, numbers, and calendars.
- Content variant model (one item, several language versions, fallback).
- Shared design primitives that are direction-aware.
- Accessibility baseline and checks.

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
3. **Given** a user preference of `fr` that the tenant has not enabled, **Then** the tenant default is used.
4. Resolution order: user preference (if enabled) > tenant default > `en`.

### User Story 2: Mixed-direction content (Priority: P1)

**Acceptance scenarios:**
1. **Given** an Arabic paragraph containing an English product name and a number, **When** rendered, **Then** text order is correct (bidi isolation applied to user-generated inline content).
2. **Given** an English page inside an Arabic UI, **Then** the content block is rendered LTR within the RTL shell.

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

## 5. Functional requirements

- **FR-001**: Supported locales MUST be `ar`, `en`, `fr`, `ur`, `hi`. Tenants enable a subset. *(LOC-001)*
- **FR-002**: `ar` and `ur` MUST render right-to-left; others left-to-right. Direction is resolved per rendered locale, never assumed from tenant default. *(LOC-002)*
- **FR-003**: No user-facing literal strings in code. All strings come from resource catalogues with keys. *(LOC-003)*
- **FR-004**: Missing translations fall back to `en` in non-production and are reported by a CI check that fails on missing keys for locales marked "launch-complete".
- **FR-005**: Content items MUST support language variants, a fallback rule and per-variant status. *(LOC-004)*
- **FR-006**: User-generated content MUST stay in its original language unless translation is requested.
- **FR-007**: Shared UI primitives MUST use logical properties (start/end, not left/right) and mirror directional icons.
- **FR-008**: Primary employee and admin journeys MUST meet WCAG 2.2 AA. *(EXP-008)*
- **FR-009**: Emails and notifications MUST be rendered in the recipient's resolved locale.
- **FR-010**: Fonts MUST cover Arabic, Urdu (Nastaliq or Naskh, see open question), Devanagari and Latin, and be self-hostable for on-prem.

## 6. Business and validation rules
- A locale is "launch-complete" when every UI key has a reviewed translation and native-speaker sign-off (SC-003). FR-004 fails CI on missing keys only for launch-complete locales.
- The tenant default locale must be one of the tenant's enabled locales (spec 001).
- Pseudo-localization is available in Development and Test only.

## 7. Tenant scope
Tenant-enabled locales and default are tenant settings (spec 001). User preference is per tenant membership.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Personal data? |
|---|---|---|---|
| Resource catalogues (UI strings) | Product (source control) | Public | No |
| User locale preference | Product | Internal | Yes |
| Content variants | Owning content module (009, 011, 025) | Same as the item | Per item |

## 9. Package entitlements
All five locales and accessibility are available in every package. No capability key.

## 10. UX
- Language switcher in the header and profile.
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
