# Feature Specification: TeamBeats Design Language System

| Field | Value |
|---|---|
| Spec ID | 036-design-language-system |
| Area | experience |
| Status | Approved |
| Version | 1.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (design guidelines and technical guidelines, four selectable designs Beats, Atlas, Pulse and Studio, design picker in the brand manager, component and template library with the living gallery, design consistency gate, design test suite with accessibility, responsive, target size and visual regression checks, enforcement in the constitution and specs, the extended component catalogue of DLS-030 to DLS-041 with its data, assistant and chart parts), R2 (design review workflow in the theme lifecycle of spec 033, cross-browser automation, quarterly design audit report), R3 (tenant design extensions through reviewed token extensions) |
| Last updated | 2026-11-10 |
| Approved | 2026-11-09 by Mohamed (standing approval to proceed) |
| Requirement IDs | DLS-001 to DLS-055 (new); CX-BRD-003, CX-BRD-006, CX-BRD-009 (with spec 033); CX-ACC-001 to CX-ACC-005 (with spec 007) |
| Use cases | UC-01 (tenant setup, design choice), every employee and admin journey (look and feel) |
| Depends on | 007 (localization and accessibility), 008 (shell), 032 (templates and components), 033 (tokens, brands, resolver, brand manager), ADR-0014, ADR-0019, ADR-0021 |
| Used by | Every spec with a user interface: its UX section references this system instead of describing visuals from scratch |

Guidelines: [docs/design-system](../../docs/design-system/README.md).

## 1. Problem and outcome

**Problem:** TeamBeats grew screen by screen. Without one written design language, each feature chose its own spacing, wording, icons and states, contrast was checked only in places, and customers could change colors but not choose a look that fits them (a ministry wants something formal, a retail group something bold). Designers had no shared rules to test against and developers had no gate to stop drift.

**Outcome:** One design language system, modeled on national design systems such as the UAE Design System, with design guidelines (typography, colour, icons, imagery, content, layout, actions, accessibility), technical guidelines (frontend practice, security, performance, consistency), ready components and templates, and a testing strategy for designers and developers. TeamBeats ships four complete designs that tenant administrators choose from; the current v2 look (Beats) is the default. The component library covers what a modern product interface needs (actions, selection, text entry, pickers, dates, overlays, structure, status, data blocks, assistant parts, charts and page patterns), each built once on the tokens so every design gets it. The build enforces the system, and the constitution makes it binding.

## 2. Scope

**In scope:**
- Design and technical guidelines in `docs/design-system`.
- Four designs: Beats (default), Atlas, Pulse, Studio, each a complete accessible token set plus a navigation style.
- Design choice per brand in the brand manager (spec 033), inherited and lockable like any token.
- Shared component library and gallery: buttons, text boxes, text areas, selects, cards, badges, avatars, alerts, images, tabs, icons, states, skeletons, confirmations, selection bar, messages.
- The extended catalogue (section 5, DLS-030 to DLS-041): actions, selection controls, text entry, pickers and menus, dates, overlays, structure, status, data blocks, assistant and agent parts, charts and page patterns. The catalogue was drawn up from a study of the BoardUI component library; every component is TeamBeats code on TeamBeats tokens, and no third-party component code or component library is used.
- Page template patterns mapped to the spec 032 templates.
- Automated consistency gate and design test suite; manual design testing process.
- Enforcement: constitution principle XXXI, spec template, CLAUDE.md, references in specs 007, 008 and 033.

**Out of scope:**
- Customer-specific designs written in code. Customers adjust a design through tokens (spec 033).
- Native mobile app design (spec 030 adopts these guidelines when it starts).
- Marketing website design.
- An image generation screen: no approved spec has that feature, so no component is built for it.

## 3. Actors and permissions

| Actor | What they can do in this feature | Permission / scope required |
|---|---|---|
| Tenant administrator, brand manager | Choose the design for the tenant brand, adjust tokens on top, publish | `theme.edit`, `theme.publish` (spec 033) |
| Organization administrator | Choose a design for their subtree when organization branding is on and the design is not locked | `theme.edit`, `theme.publish` at org scope, `experience.org_branding` |
| Employee | Sees the effective design; chooses mode, density, text size and motion (spec 008) | Tenant membership |
| Designers, UX writers, researchers | Apply the guidelines and run design tests | Not a product role |
| Developers | Build with the components and pass the gates | Not a product role |

## 4. User scenarios

### User Story 1: Choose a design for the workspace (Priority: P1, R1)

A ministry's tenant administrator opens Administration, Brand, sees four design cards with live miniatures, chooses Atlas, checks the preview and contrast table, and saves. Every employee then sees the formal green design with top navigation, in Arabic and English, light and dark.

**Independent test:** set `design.preset` to each design on a test tenant through the brand API; the home and gallery show the design's attributes, fonts and accent, with no serious accessibility violation.

**Acceptance scenarios:**
1. **Given** the brand manager, **When** it opens, **Then** the four designs show with name, description, navigation style and a miniature drawn from the design's own resolved tokens, and the current design is marked. *(DLS-001)*
2. **Given** an administrator chooses a design, **Then** the editor replaces the token values changed so far with the design, keeps the brand name and images, reruns the checks and shows the preview; nothing reaches employees until saved. *(DLS-002)*
3. **Given** a saved design, **Then** pages render with that design's tokens and navigation style, and the brand version and audit record name the change. *(DLS-003)*
4. **Given** a tenant on Atlas sets its own accent color, **Then** the accent wins over the design and every other value stays Atlas. *(DLS-004)*
5. **Given** the tenant locked `design.preset`, **When** an organization chooses another design, **Then** the child choice is ignored and the impact view says so. *(DLS-005)*

### User Story 2: Build a screen with the system (Priority: P1, R1)

A developer builds a new page from a template and the shared components; the build fails when they use a raw colour, an inline SVG icon, an image without alternative text or a literal string.

**Acceptance scenarios:**
1. **Given** a component outside the system's rules, **When** `npm run verify` runs, **Then** it fails with the rule and the fix. *(DLS-010)*
2. **Given** the gallery, **Then** it shows every shared component, the icon set, the colour and type tokens and the template patterns in the tenant's active design. *(DLS-008)*

### User Story 3: Test the design (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a pull request, **When** CI runs the design suite, **Then** each design in each language passes axe with no serious or critical violation, has no horizontal scrolling at 375, 768 and 1440 px, has no control under 24 by 24 px, and matches the reviewed gallery baselines. *(DLS-012 to DLS-015)*
2. **Given** a feature's first build, **Then** the designer and developer run the post-development review with generated screenshots and record the outcome on the pull request. *(DLS-017)*

### Edge cases and exception flows
- An older brand names a design that no longer exists: the default design applies and the token appears in the invalid token log (spec 033 fallback).
- A tenant changes an Atlas token so a pair fails contrast: the existing spec 033 blocking or warning rules apply.
- A font file fails to load: the fallback stack of the script renders.
- A design is chosen at organization scope without `experience.org_branding`: refused as any organization override is.

## 5. Functional requirements

- **DLS-001**: The brand manager MUST present the four designs with a live miniature, description and navigation style, and mark the current one.
- **DLS-002**: Choosing a design MUST be a token change (`design.preset`) validated, checked, versioned, audited and reversible like any brand change (spec 033 FR-012).
- **DLS-003**: The theme resolver MUST apply the design's values between the platform defaults and the tenant brand (spec 033 FR-030).
- **DLS-004**: Brand and organization values MUST win over the design's values.
- **DLS-005**: `design.preset` and `layout.navigation` MUST be lockable and inheritable like any token.
- **DLS-006**: Each design MUST define every colour in light and dark mode and pass every contrast and text size check of spec 033 FR-009 in strict mode.
- **DLS-007**: The four designs MUST differ in palette, typography, shape and elevation, and MAY share a navigation style (Pulse and Studio both use the labelled sidebar, dark and light). They MUST NOT differ in behavior, content, permissions or information architecture.
- **DLS-008**: The web app MUST provide shared components for actions, text boxes, selections, cards, badges, avatars, alerts, images, tabs, icons and states, and a gallery showing them in the active design.
- **DLS-009**: Components MUST read only `--tb-*` tokens; per-design structural rules MUST live in one file.
- **DLS-010**: The build MUST fail on raw colours or sizes, literal font families or sizes, inline SVG outside the icon set, images without alternative text, literal user-facing strings and missing translations.
- **DLS-011**: Images MUST render with a fixed aspect ratio and lazy loading below the fold.
- **DLS-012**: CI MUST run axe on the home and gallery in every design and launch language, failing on serious or critical violations.
- **DLS-013**: CI MUST check for horizontal scrolling at phone, tablet and desktop widths in every design and direction.
- **DLS-014**: CI MUST check that interactive controls in the gallery are at least 24 by 24 CSS pixels.
- **DLS-015**: CI MUST compare gallery sections against reviewed visual baselines per design and direction.
- **DLS-016**: The design guidelines MUST cover typography, colour, icons, imagery, content, layout, actions and accessibility; the technical guidelines MUST cover frontend practice, security, performance and consistency.
- **DLS-017**: The testing strategy MUST define user-centric, accessibility, content, responsive, post-development and consistency tests with owners and gates.
- **DLS-018**: Every spec with a user interface MUST reference the design system in its UX section and list any new component or token it needs.
- **DLS-019**: New components, tokens or designs MUST update the guidelines, gallery and tests in the same change.
- **DLS-020**: Fonts MUST be self-hosted from the approved list and loaded per script on demand (ADR-0019, ADR-0021).
- **DLS-021**: All four designs MUST be available in every package.
- **DLS-022**: The design suite SHOULD write review screenshots on request for the post-development review.
- **DLS-025**: Every page MUST sit in the page frame: content up to 90rem wide, centred, with `space.lg` side padding (`space.md` on phones). Full-bleed sections paint to the edge but align their content to the frame.
- **DLS-026**: Module pages MUST be built from four page templates (List, Catalogue, Directory, Detail) and the shared page parts: page header (icon tile, h1, description, actions), section tabs, filter bar, data table, tile card, list rows, person card and split view (main column with a side column).
- **DLS-027**: Links MUST use `color.link` and carry no underline when their position says they are links (titles, tabs, navigation, cards); links inside running text keep an underline.
- **DLS-028**: The home MUST use the briefing hero (night surface), Your day tiles in the warm, cool and fresh tints, featured news and the campaign band, each from shared tokens only (`color.tint.*`, `color.surface.night`, `color.text.on_night`, `color.highlight.bright`, `color.surface.campaign`), with every pair checked for contrast in every design and mode.
- **DLS-029**: Names and other values that may be in another script MUST be isolated in sentences (`isolate()` in `lib/bidi.ts`, or `<bdi>` in markup) so an Arabic name does not reorder an English sentence and the other way round.
- **DLS-030**: Studio MUST be a design like the others: a token set chosen with `design.preset`, neutral surfaces, one blue accent, Inter with IBM Plex Sans Arabic, hairline borders and a light labelled sidebar. Its finish (a soft shade and a light top edge on filled buttons and the current sidebar page) MUST be drawn from its own tokens, so a tenant accent keeps the finish.
- **DLS-031**: Every component of the catalogue MUST be built once in `src/Web/components/ui` (parts used by one feature only in that feature's folder), read only `--tb-*` tokens, take all text from messages, and look right in every design, both directions and both modes. Where a component for the pattern exists it MUST be extended, not duplicated.
- **DLS-032**: Interactive behavior MUST use native elements first (`button`, `input`, `select`, `details`, `dialog`, the Popover API) and shared hooks for what the platform lacks (roving focus, type ahead, dismissal, focus return). The web app MUST NOT add a component library, a CSS framework or a behavior library for these components (constitution XXII).
- **DLS-033**: Actions: Button MUST offer the sizes medium (44 px), small (36 px) and compact (28 px, only inside toolbars, tables and dense panels), a leading or trailing icon, an icon-only form with a required accessible name, and a link form. The system MUST provide a button group, an icon button and a close button.
- **DLS-034**: Selection: checkbox (with indeterminate), radio group, switch, each also as a card with a description; segmented control; slider; chip (static, removable, selectable) and tag group; all operable by keyboard as their WAI-ARIA patterns describe.
- **DLS-035**: Text entry: text boxes with leading and trailing parts, search field with clear, number field with steppers, one-time code field with paste, time field, color field with presets, and file upload with drag and drop, validation messages and progress. Validation rules come from the API; the components only show them.
- **DLS-036**: Pickers and menus: menu (with groups, checked items and submenus), context menu, combobox with filtering, command palette opened from the shell search shortcut, and tree view. Date components (calendar, date field, date range field, slot picker) MUST format through `Intl` in the user's locale and the tenant's calendar, including the Hijri calendar, and MUST mirror in right to left.
- **DLS-037**: Overlays: dialog, drawer from any edge, popover, tooltip, announcement banner and the notification card form of messages. Overlays MUST trap and return focus, close on Escape, and never open over the element that has focus without moving focus into them.
- **DLS-038**: Structure and status: accordion, breadcrumb with collapsing levels, pagination, divider, carousel with snap and visible controls, tabs with a sliding indicator, icons and counts, sortable and selectable data table; avatar groups and presence, status dot, keyboard key, spinner, progress and meter.
- **DLS-039**: Data blocks: data table with toolbar, filters, column visibility and paging; data grid; stat cards with change against the previous period; month and week calendar; task list; notification panel; settings dialog; sign-in card. Assistant and agent parts: thinking indicator, composer with attachments, source list, usage limits, question steps, agent form and run progress. Blocks receive data and callbacks from their feature and hold no business rule.
- **DLS-040**: Charts MUST be TeamBeats SVG components inside a chart card (area, line, bar, bar list, combined, funnel, heat map, radar, radial, flow, scatter, rings, contributions, stage bars, steps), colored only from categorical chart tokens (`color.chart.1` to `color.chart.8`, `color.chart.track`) defined in every design for both modes, and MUST carry a text or table alternative. Color MUST NOT be the only way to tell series apart.
- **DLS-041**: Motion finish: selection indicators slide, filled buttons shade on hover, overlays fade and move a short distance, all with the motion tokens and only `transform` and `opacity`; everything stops when the user asked for reduced motion.
- **DLS-042**: Detail quality: every screen MUST pass the detail checklist in `docs/design-system/foundations/details.md` before it merges, and the design suite MUST cover the shell panels, list rows, forms and empty states it names.
- **DLS-043**: Values MUST be shown in people's words, never as machine values: languages through `Intl.DisplayNames`, dates and times through `Intl.DateTimeFormat` in the reader's locale (long style on profiles and articles, medium in lists), times within the last week as relative time with the full date on hover, time zones by city name and offset, and numbers through `Intl.NumberFormat`. Codes such as `en-US`, `2015-11-01` or `Africa/Cairo` MUST NOT reach the screen.
- **DLS-044**: Links placed as titles, rows, breadcrumbs, menus and chips MUST NOT be underlined (DLS-027); a link inside a sentence that starts an action (connect, edit, view) MUST be a button instead. Breadcrumbs MUST use muted text with chevron separators that mirror in right to left, and the current page in primary text.
- **DLS-045**: Shell panels (account menu, notifications, settings) MUST share one popover surface: the same radius, border, shadow and inner padding, a header row with the title and at most one action, a scrolling body with a thin scrollbar, and an optional footer bar with a divider. Menu entries are full-width rows with a leading icon and hover and focus fills; destructive entries such as Sign out come last after a divider.
- **DLS-046**: A notification row MUST show a category icon tile, the title in regular weight clamped to two lines, and one muted meta line (category, a middle dot, relative time). Unread MUST be a small accent dot plus a medium weight title, never a tinted box; mandatory items carry a warning badge.
- **DLS-047**: Settings with two to five short options (mode, density, motion, text size) MUST use the segmented control; selects are for longer lists such as language and time zone. Settings MUST be grouped under small section headings.
- **DLS-048**: Status MUST be a badge with a tone that follows the state (success for approved and completed, warning for returned and overdue, danger for rejected, info for in progress, neutral for drafts and cancelled), never coloured text alone and never an outlined box.
- **DLS-049**: Rows in a list MUST use the list rows part: one title link without underline, a status badge, one muted meta line whose facts are separated by a middle dot, and a trailing chevron when the row opens a page. Separate facts MUST NOT run together in one string.
- **DLS-050**: Floating actions such as Ask TeamBeats MUST NOT cover content at the end of a page: the page frame keeps space for them at its end, and on phones they shrink to an icon button with a visible tooltip and the same accessible name.
- **DLS-051**: Form controls: hints MUST sit under their control so a row of controls lines up; inside a filter bar a hint becomes the placeholder. Fields in a row lose their bottom margin so buttons line up with controls. Native radios and checkboxes MUST get the system look (size, border, accent fill, focus ring); a yes or no answer such as "Was this helpful?" MUST be two toggle buttons; a sort direction MUST be an icon button with `aria-pressed`, not a checkbox. Required fields carry a small "Required" tag.
- **DLS-052**: Buttons MUST keep their natural width; one primary action per area, start aligned with the secondary actions beside it at the same height. Full-width buttons are only for phones and sign-in cards.
- **DLS-053**: Empty, error and denied states MUST show an icon tile, a title and one line of help, without a dashed border, and an action when there is one. A card that would otherwise end half empty MUST fill its space with its empty state.
- **DLS-054**: Forms and reading pages MUST sit in a readable column (forms at most 44rem, article text at most 46rem) inside the page frame; detail pages use the page header with the record's icon and breadcrumbs, not the section tabs of the area.
- **DLS-055**: Person cards and other cards with an avatar MUST align the avatar to the top of the text, so names line up whether a card has one line or three.
- **DLS-023**: R2: theme drafts (spec 033 US2) SHOULD show the design review checklist before approval.
- **DLS-024**: R2: cross-browser runs (Firefox, WebKit) SHOULD join the design suite once the browser matrix decision in `docs/testing/e2e-plan.md` is made.

## 6. Business and validation rules
- `design.preset` takes `beats`, `atlas`, `pulse` or `studio`; `layout.navigation` takes `rail`, `top` or `sidebar`. Other values are refused with `validation.theme.choice_invalid`.
- A new tenant, and any brand that sets no design, uses Beats.
- Choosing a design in the editor replaces the brand's token values; brand name, assets and contrast policy stay.

## 7. Tenant and organization scope
- The design is a token of the tenant brand profile and of organization overrides; it follows spec 033 section 7 for isolation, locks and inheritance.
- Cross-tenant denial: the same cases as spec 033 (theme CSS and assets of another tenant are not found).

## 8. Data, ownership and classification
No new data. The design is stored as a token in the brand version (spec 033 section 8).

## 9. Package entitlements
All four designs are in every package (smallest safe default: the design is part of tenant branding, which spec 033 puts in every package). Organization-level design choice needs `experience.org_branding`.

## 10. UX, localization and accessibility
- The guidelines in `docs/design-system` are the UX rules for every screen.
- Design names, descriptions and picker text are localized in Arabic and English.
- All four designs meet WCAG 2.2 AA in both directions and modes.

## 11. AI behavior and data policy
Not applicable. The AI mark (four point star) is reserved for AI features in every design.

## 12. Integrations and events
Design changes publish through `ThemePublished` like any brand change.

## 13. Audit, telemetry and analytics
`theme.published` audit records include the new brand version; the design is visible in the version's tokens. Invalid design values increment `theme.invalid_token`.

## 14. Security and abuse cases
- A design is a fixed, reviewed token set: it cannot add CSS, scripts or remote resources.
- Per-design CSS reads only tokens; fonts come only from the app's own origin under the CSP.

## 15. Non-functional requirements
- Theme CSS stays under 30 KB compressed per brand and mode with the new tokens.
- Fonts of designs a tenant does not use are not downloaded.
- Switching design causes no layout shift on load (theme in the head before paint).

## 16. Success criteria
- 100 percent of shared components appear in the gallery and pass the design suite in four designs and two languages.
- Zero serious or critical axe violations on the home and gallery in every design.
- A tenant administrator can switch design in under one minute without help.

## 17. Open questions
- Should Firefox and WebKit join the design suite in R1 or R2? Default: R2 with the browser matrix decision.
- Should tenants be able to submit their own design? Default: no; they adjust one of the four through tokens; revisit with R3 token extensions.
- Is Studio the final name of the fourth design? Default: Studio, so no other company's brand name appears in the product.

## 18. Traceability
| Requirement | Tests |
|---|---|
| DLS-002 to DLS-007 | `tests/Unit/DesignPresetTests.cs`, `ThemeEngineTests.cs`, `WebTokensFileTests.cs` |
| DLS-001, DLS-003, DLS-012 to DLS-015, DLS-022 | `tests/Acceptance/specs/design-system.spec.ts` |
| DLS-008 to DLS-011 | `src/Web/scripts/check-design.mjs`, `check-tokens.mjs`, stylelint, ESLint |
| DLS-016, DLS-017 | `docs/design-system` |
| DLS-030 | `tests/Unit/DesignPresetTests.cs`, `tests/Acceptance/specs/design-system.spec.ts` (Studio) |
| DLS-042 to DLS-055 | `tests/Acceptance/specs/design-system.spec.ts` (gallery details section), `tests/Acceptance/specs/details.spec.ts` (shell panels and pages), `src/Web/lib/format.test.ts`, `npm run verify` |
| DLS-031 to DLS-041 | `tests/Acceptance/specs/design-system.spec.ts`, `tests/Acceptance/specs/components.spec.ts` (keyboard behavior per component), `npm run verify` |

## Review checklist
- [x] No implementation detail beyond what the contract needs
- [x] Acceptance scenarios testable
- [x] Tenant, organization and package rules stated
- [x] Accessibility and localization covered
