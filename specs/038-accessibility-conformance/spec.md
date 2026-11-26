# Feature Specification: WCAG 2.2 AA Accessibility Conformance

| Field | Value |
|---|---|
| Spec ID | 038-accessibility-conformance |
| Area | experience |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R2 (conformance map of every WCAG 2.2 A and AA success criterion, the conformance matrix and its CI gate over every route in four designs, two modes, two languages and two widths, keyboard, focus, target size, reflow, zoom, text spacing and motion checks on real pages, static accessibility lint, drag alternatives, accessibility statement page, conformance report draft), R3 (assistive technology audits by a qualified auditor before each release, tagged PDF output for generated letters and reports, cross-browser accessibility runs in Firefox and WebKit) |
| Last updated | 2026-11-18 |
| Approved | 2026-11-18 under Mohamed's standing approval of 2026-11-07 (request of 2026-11-18 in the thread "Add WCAG 2.2 AA accessibility") |
| Requirement IDs | A11Y-001 to A11Y-024 (new); EXP-008 and CX-ACC-001 to CX-ACC-014 (verification part, owned by 007) |
| Use cases | All employee and administration journeys |
| Depends on | 007 (accessibility baseline and localization), 008 (shell, preferences), 032 (page builder), 033 (themes, modes), 036 (design language system and design suite) |

## 1. Problem and outcome

**Problem:** Spec 007 sets WCAG 2.2 AA as the bar and spec 036 builds accessible parts, but conformance is only partly proven. The CI gate blocks only critical axe findings on most pages; serious findings block only on the home and the galleries. Dark mode, the phone width and three of the four designs are not checked on the main pages, many routes are never scanned, keyboard and focus behavior is tested on a few journeys only, and the success criteria that automated rules cannot see (focus not obscured, target size on real pages, text spacing, zoom, dragging) have no test. Government and enterprise buyers ask for a conformance report against WCAG 2.2 AA, and TeamBeats has none.

**Outcome:** Every WCAG 2.2 level A and AA success criterion has a recorded way TeamBeats meets it and a recorded way it is verified. Every route of the web app passes an automated conformance gate in CI with zero critical and zero serious violations, in every design (Beats, Atlas, Pulse, Studio), in light and dark, in Arabic right to left and English, at desktop and phone width. Real-page checks cover what axe cannot: keyboard reach, visible and unobscured focus, target size, reflow at 320 CSS pixels, 200 percent text, text spacing and reduced motion. A regression in any of these fails the build. A conformance report and a public accessibility statement say honestly what is met, what is partial and what still needs a manual audit.

## 2. Scope

**In scope:**
- A conformance map of all 55 WCAG 2.2 level A and AA success criteria (4.1.1 is obsolete in WCAG 2.2 and is listed as such).
- The conformance matrix and its CI gate over every route of the web app, signed in and signed out.
- Real-page checks for keyboard, focus, target size, reflow, zoom, text spacing and motion.
- Static accessibility lint in the web build.
- Non-drag alternatives for every drag interaction (page builder, home arrange mode, process modeler, boards).
- The accessibility statement page and the conformance report (ACR in the VPAT 2.5 WCAG edition format).
- The manual audit protocol with assistive technologies, and who signs it.

**Out of scope:**
- Level AAA criteria. Where TeamBeats already goes further (44 pixel targets, DLS-033) that stays, but it is not a conformance claim.
- Native mobile apps. The mobile experience is the installable web app of spec 030, which this spec covers.
- Content that tenants write. TeamBeats checks and flags it (spec 007 FR-026, spec 032 FR-046), but a tenant's own words, images and uploaded documents are the tenant's responsibility; the statement says so.
- Third-party pages a connector opens (Microsoft 365, Google sign-in pages).

## 3. Actors
Employee (including people who use screen readers, magnification, voice control, switch access or keyboard only), tenant administrator, content editor, platform operator, developer (keeps the gate green), accessibility auditor (manual audit, R3), buyer or procurement officer (reads the report).

## 4. User scenarios

### User Story 1: Every route passes the conformance gate (Priority: P1)

**Independent test:** run the accessibility suite; it visits every route in the matrix and reports zero critical and zero serious violations.

**Acceptance scenarios:**
1. **Given** any route of the web app, **When** the suite opens it in its assigned combination of design, mode, language and width, **Then** axe with the WCAG 2.0, 2.1 and 2.2 A and AA rules reports no critical and no serious violation.
2. **Given** the primary pages (A11Y-003), **When** the suite runs, **Then** each is checked in all 32 combinations of four designs, two modes, two languages and two widths.
3. **Given** a change that lowers the contrast of a token in dark mode in one design, **When** CI runs, **Then** the gate fails and names the page, the combination and the element.
4. **Given** a moderate or minor finding, **Then** it is attached to the report and listed in the conformance map's known issues, and it does not block the build.
5. **Given** a new route added to the web app without an entry in the route inventory, **When** CI runs, **Then** the inventory check fails until the route is listed (A11Y-004).

### User Story 2: Keyboard and focus on real pages (Priority: P1)

**Acceptance scenarios:**
1. **Given** a primary page, **When** a keyboard user presses Tab from the top, **Then** the first stop is the skip link, and activating it moves focus to the main region.
2. **Given** the Tab sequence on a primary page, **Then** every focusable element shows a visible focus indicator of at least 2 CSS pixels with 3:1 contrast against its background, in every design and mode.
3. **Given** a focused element, **Then** it is not entirely hidden by a sticky header, a sticky toolbar, the bottom navigation or a floating action (WCAG 2.4.11).
4. **Given** a dialog, menu or bottom sheet, **When** it opens, **Then** focus moves into it, Tab stays inside it, Escape closes it and focus returns to the control that opened it.
5. **Given** a keyboard user, **Then** no Tab sequence traps focus and no positive `tabindex` exists.

### User Story 3: Size, zoom, spacing and motion (Priority: P1)

**Acceptance scenarios:**
1. **Given** a primary page at 320 CSS pixels wide, **Then** no content needs horizontal scrolling, except data tables, maps, charts and the calendar grid, which scroll inside their own region (WCAG 1.4.10).
2. **Given** the personal text size at 200 percent, **Then** no text is clipped or overlapped and no function is lost (WCAG 1.4.4).
3. **Given** the WCAG text spacing values (line height 1.5, paragraph spacing 2 times the font size, letter spacing 0.12 and word spacing 0.16 times the font size), **Then** no text is clipped inside a container that hides overflow (WCAG 1.4.12).
4. **Given** any interactive target on a primary page, **Then** it is at least 24 by 24 CSS pixels, or has the spacing that the WCAG 2.5.8 exceptions allow, or is an inline link in a sentence.
5. **Given** reduced motion from the device or the personal setting, **Then** no animation longer than 200 milliseconds runs and nothing scrolls smoothly.
6. **Given** content that appears on hover or focus (tooltips, previews), **Then** it can be dismissed with Escape, the pointer can move onto it, and it stays until dismissed or no longer relevant (WCAG 1.4.13).

### User Story 4: Every drag has a single pointer alternative (Priority: P1)

**Acceptance scenarios:**
1. **Given** the page builder, home arrange mode, process modeler or a board, **When** a person cannot drag, **Then** they can move each item with buttons or a menu (move up, move down, move to region or column) using one pointer click or the keyboard (WCAG 2.5.7).
2. **Given** a move made without dragging, **Then** the result is announced in a live region in the page language.

### User Story 5: Forms, errors and authentication (Priority: P1)

**Acceptance scenarios:**
1. **Given** a form with an invalid field, **When** it is submitted, **Then** the error is in text next to the field, linked with `aria-describedby`, the field has `aria-invalid`, the first invalid field takes focus, and the input is kept.
2. **Given** a multi-step form or journey, **Then** information the person already gave in the same process is filled in or offered for selection, not asked again (WCAG 3.3.7).
3. **Given** sign-in, **Then** it never needs a cognitive function test: the identity provider handles credentials, the development and staging forms allow paste and password managers, and step-up uses the provider's methods (WCAG 3.3.8).
4. **Given** a request, approval or submission with legal or financial effect (leave, expenses, letters, data changes, policy acknowledgement), **Then** the person can review and correct before submitting, or reverse afterwards (WCAG 3.3.4).

### User Story 6: Statement and report (Priority: P2)

**Acceptance scenarios:**
1. **Given** anyone, signed in or not, **When** they open `/accessibility`, **Then** they see the TeamBeats accessibility statement in their language: the target (WCAG 2.2 AA), how it is checked, the date of the last check, known limitations, and how to report a problem.
2. **Given** a tenant that configured an accessibility link in its footer (spec 008 FR-056), **Then** the statement shows that link as the organization's own contact, and the footer link stays as configured.
3. **Given** a tenant whose footer has no accessibility link, **Then** the footer offers the TeamBeats statement, and the statement says to contact the organization's TeamBeats administrator. A tenant with no footer at all keeps none (spec 008 FR-056); the statement is still reachable at its address and from the sign-in page.
4. **Given** a buyer, **Then** the conformance report in `docs/accessibility/conformance-report.md` states for every criterion Supports, Partially supports, Does not support or Not applicable, with remarks, and is never more favorable than the conformance map.

## 5. Functional requirements

### Conformance map and inventory
- **A11Y-001**: `docs/accessibility/wcag-2.2-conformance.md` MUST list every WCAG 2.2 level A and AA success criterion with how TeamBeats meets it, how it is verified (automated gate, real-page check, lint, unit test or manual audit), the test that proves it, and its status. A criterion verified only manually MUST say so.
- **A11Y-002**: The conformance report MUST be derived from the conformance map. A criterion MUST NOT be reported as Supports while the map shows an open serious finding or no verification for it.
- **A11Y-003**: The primary pages are sign-in, home, tasks, services, requests, calendar, assistant, people, policies, documents, knowledge, notifications, search, settings, leave, the administration overview, users and plan, and the three component galleries.
- **A11Y-004**: A route inventory MUST list every page route of the web app with its tier (primary or other) and any setup it needs. A check MUST fail when a page route exists in `src/Web/app` that the inventory does not list.

### The gate
- **A11Y-005**: The conformance matrix is four designs, light and dark mode, Arabic and English, and desktop (1280 by 900) and phone (390 by 844) widths. Primary pages MUST run in all 32 combinations. Every other route MUST run in at least one combination per language, with design, mode and width rotated across routes so that every value of every dimension is covered on many routes (pairwise coverage).
- **A11Y-006**: The gate MUST fail on any critical or serious axe violation with the WCAG 2.0, 2.1 and 2.2 A and AA tags. Moderate and minor findings MUST be attached to the report.
- **A11Y-007**: The gate MUST run on every pull request as its own CI job, separate from the functional e2e job, sharded so that it finishes within the job limit.
- **A11Y-008**: Exclusions MUST be listed in one file with the rule, the selector, the reason and a review date. An exclusion without a reason or past its review date MUST fail the suite. Disabling a rule globally is not allowed.

### Real-page checks
- **A11Y-009**: Keyboard: on primary pages the skip link MUST be the first Tab stop and move focus to `main`; every Tab stop MUST show a focus indicator (outline or ring) at least 2 CSS pixels thick; no `tabindex` above 0.
- **A11Y-010**: Focus not obscured: on primary pages, at desktop and phone width, no Tab stop MAY be entirely covered by a fixed or sticky element (header, toolbar, bottom navigation, floating action). Scrolling MUST keep a scroll margin for sticky regions.
- **A11Y-011**: Target size: on primary pages every visible button, link styled as a control, tab, checkbox, radio, switch, select and input MUST be at least 24 by 24 CSS pixels, except inline links in running text and controls whose size the user agent decides.
- **A11Y-012**: Reflow: at 320 CSS pixels wide no primary page MAY scroll horizontally; tables, charts, maps, code and the calendar grid scroll inside their own focusable region with an accessible name.
- **A11Y-013**: Zoom: with the personal text size at 200 percent (spec 008), primary pages at desktop width MUST NOT scroll horizontally and MUST NOT clip text.
- **A11Y-014**: Text spacing: with the WCAG 1.4.12 spacing applied, no text on primary pages MAY be cut off by a container that hides overflow, except where the full text is available in the accessible name or a tooltip.
- **A11Y-015**: Motion: with reduced motion, no running animation or transition MAY last longer than 200 milliseconds on primary pages.
- **A11Y-016**: Dialogs, menus, popovers and bottom sheets MUST trap focus while open, close on Escape and return focus to their trigger; a check MUST cover each overlay part of spec 036 in the component gallery.

### Build-time checks
- **A11Y-017**: The web lint MUST apply the recommended `jsx-a11y` rules that `eslint-config-next` already ships, at error level, with any rule turned off listed with its reason in the lint configuration.
- **A11Y-018**: Token contrast checks (spec 033 FR-009, spec 036) MUST cover every design in both modes, including focus ring against surface and every status colour against its background.

### Interaction rules
- **A11Y-019**: Every drag interaction MUST offer a single pointer, non-drag alternative with the same result, and announce the result (WCAG 2.5.7, CX-ACC-003).
- **A11Y-020**: Help MUST be found in the same relative place on every page: the Ask TeamBeats entry, the account menu and the footer accessibility link stay in the same place and order on every page that shows them, in each design (WCAG 3.2.6).
- **A11Y-021**: Processes that ask for information MUST NOT ask again for what the person entered earlier in the same process, unless it is needed for security or the earlier value is no longer valid (WCAG 3.3.7).
- **A11Y-022**: Authentication MUST NOT need a cognitive function test, and every TeamBeats sign-in field MUST allow paste and autofill (`autocomplete` set) (WCAG 3.3.8, 1.3.5).

### Statement and audit
- **A11Y-023**: A public route `/accessibility` MUST show the accessibility statement in the person's language, signed in or not, with the tenant's own accessibility link when configured. A configured footer MUST link to it when the tenant gave no accessibility link of its own, and the sign-in page MUST link to it. All text comes from resource keys.
- **A11Y-024**: Before each release from R3, a qualified auditor MUST test the primary journeys with NVDA and JAWS on Windows, VoiceOver on macOS and iOS, and TalkBack on Android, in Arabic and English, and the conformance map and report MUST be updated from the findings. Until then the report says that assistive technology testing is internal only.

## 6. Business and validation rules
- A violation in a component is fixed in the component, never by excluding the page.
- Color contrast exceptions are allowed only for disabled controls, logos and decoration, as WCAG defines.
- When a tenant's brand makes a page fail contrast, the brand editor blocks publishing the brand (spec 033); the gate runs on the platform designs, not on tenant brands.
- A route that needs data to show its real content (a record page, a detail page) is checked with seeded data, not only in its empty state.

## 7. Tenant scope
No tenant data is created by this spec. The statement reads the tenant's footer configuration (spec 008), which is already tenant scoped. The suite creates its own test tenants, as every e2e test does.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Personal data? |
|---|---|---|---|
| Conformance map and report | Product (source control) | Public | No |
| Route inventory and exclusions | Product (source control) | Internal | No |
| Accessibility statement text | Product (resource catalogues) | Public | No |

## 9. Package entitlements
Accessibility is in every package. No capability key.

## 10. UX, localization and accessibility
- The statement page uses the standard page frame, page header and content parts of spec 036, in both directions.
- Non-drag alternatives use the shared menu and icon button parts with visible labels or tooltips; move announcements use the shared live region.
- Arabic is checked as Arabic: `lang="ar"`, `dir="rtl"`, mirrored icons and Arabic accessible names.

## 11. AI behavior
Not applicable. The assistant pages are in the gate like any other page.

## 12. Integrations and events
None.

## 13. Audit
Not applicable: no tenant state changes.

## 14. Security and abuse cases
- The statement page is public and shows only the tenant's configured footer link, which is already public in the signed-out footer.
- The suite runs against the development host only and uses fictional data.

## 15. Non-functional requirements
- The accessibility job MUST finish within 35 minutes per shard.
- Checks MUST be deterministic: animations disabled where they would change results, fonts loaded before contrast is measured.

## 16. Success criteria
- **SC-001**: Zero critical and zero serious axe violations on every route in the matrix, enforced on every pull request.
- **SC-002**: Every WCAG 2.2 A and AA criterion has a status and a verification in the conformance map.
- **SC-003**: Every drag interaction has a tested non-drag alternative.
- **SC-004**: An external audit with assistive technologies finds no critical issue in the primary journeys (R3).

## 17. Open questions
1. Defaulted 2026-11-18: phone width for the axe matrix is 390 CSS pixels (the phone the design suite uses); the 320 pixel reflow check runs separately (A11Y-012). Change only if a buyer names a device.
2. Defaulted 2026-11-18: the external audit is planned for R3; R2 ships with internal keyboard and screen reader checks, and the report says so.
3. Defaulted 2026-11-18: generated PDFs (letters, reports) become tagged PDFs in R3. Built in R3 (T019): letters, learning certificates, disposition certificates and policy evidence packs print as tagged PDFs with an outline, their language and direction and a title.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| EXP-008 | A11Y-001 to A11Y-007 (verification of spec 007 FR-008) |
| CX-ACC-001 | A11Y-005, A11Y-006, A11Y-024 |
| CX-ACC-002 | A11Y-009, A11Y-016 |
| CX-ACC-003 | A11Y-019, US4 |
| CX-ACC-004 | A11Y-009, A11Y-010, A11Y-016 |
| CX-ACC-005 | A11Y-006, A11Y-018 |
| CX-ACC-006 | A11Y-013 |
| CX-ACC-007 | A11Y-012 |
| CX-ACC-008 | A11Y-006 |
| CX-ACC-010 | A11Y-015 |
| CX-ACC-013 | A11Y-005 (modes and densities in the matrix) |
| CX-ACC-014 | US5 |
