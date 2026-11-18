# WCAG 2.2 AA conformance map

Spec 038 A11Y-001. Every WCAG 2.2 level A and AA success criterion, how TeamBeats meets it, how it is verified, and its status. The conformance report (`conformance-report.md`) is derived from this map and is never more favorable than it.

Last updated: 2026-11-18. Scope: the TeamBeats web app and installable web app (spec 030), every route in `tests/Acceptance/a11y/routes.ts`, in the four platform designs, light and dark, Arabic and English, desktop and phone.

**Verification keys**
- **Gate**: axe in the accessibility CI job (`tests/Acceptance/a11y/matrix.spec.ts`), zero critical and serious.
- **Page check**: a real-page check in the accessibility or design suite (named in the row).
- **Lint**: a build-time check in `npm run verify` or the theme engine tests.
- **Manual**: keyboard and screen reader review; external audit planned for R3 (A11Y-024).

**Status keys**
- **Supports**: met, and verified as stated.
- **Partial**: met in most places, with the known gaps listed.
- **Manual only**: met by design, with no automated check possible or built yet; checked only in internal reviews until the R3 audit, so the report says Partially supports or Supports with that remark.
- **Not applicable**: the product has no content of this kind.

## Perceivable

| SC | Level | How TeamBeats meets it | Verification | Status |
|---|---|---|---|---|
| 1.1.1 Non-text content | A | Icons are decorative (`aria-hidden`) or named; icon buttons need an accessible name (DLS-033); images need alt text and publishing flags missing alt (spec 007 FR-026) | Gate (`image-alt`, `button-name`, `svg-img-alt`); Lint (`check:design` img alt) | Supports |
| 1.2.1 Audio-only and video-only (prerecorded) | A | Tenant media is the tenant's content; the player offers the transcript field of spec 009 | Manual | Manual only |
| 1.2.2 Captions (prerecorded) | A | Video blocks accept a caption track (spec 009); TeamBeats ships no video of its own | Manual | Partial: caption track not yet required on the document preview player (`DocumentPanel`) |
| 1.2.3 Audio description or media alternative | A | Transcript field on video blocks | Manual | Manual only |
| 1.2.4 Captions (live) | AA | No live media in the product | | Not applicable |
| 1.2.5 Audio description (prerecorded) | AA | Tenant content; transcript field | Manual | Manual only |
| 1.3.1 Info and relationships | A | Shared parts carry semantics: landmarks, headings, lists, tables with headers, labelled fields, fieldsets for groups | Gate (`list`, `th-has-data-cells`, `label`, `aria-*`); Page check (one `h1` per page, design suite) | Supports |
| 1.3.2 Meaningful sequence | A | DOM order follows the visual order in both directions; layouts use logical properties | Page check (RTL matrix); Lint (`lint:css` logical properties) | Supports |
| 1.3.3 Sensory characteristics | A | Instructions name controls by their label, never by shape or position (content guidelines) | Manual | Manual only |
| 1.3.4 Orientation | AA | No page locks orientation; the web app manifest leaves orientation free | Manual | Manual only |
| 1.3.5 Identify input purpose | AA | Sign-in and profile fields set `autocomplete` (A11Y-022) | Gate (`autocomplete-valid`); Page check (`authentication.spec.ts`) | Supports |
| 1.4.1 Use of color | A | Status uses text and icon with the colour (spec 007 FR-024, Badge and status parts) | Gate (`link-in-text-block`); Manual | Supports |
| 1.4.2 Audio control | A | Nothing plays on its own; read aloud starts only on request (spec 014) | Manual | Manual only |
| 1.4.3 Contrast (minimum) | AA | Token contrast enforced for every design and mode; brand editor blocks failing brands | Gate (`color-contrast`, all designs and modes); Lint (`ThemeEngineTests`, `DesignPresetTests`) | Supports |
| 1.4.4 Resize text | AA | Personal text size up to 200 percent scales the root font (spec 008) | Page check (`interaction.spec.ts`, 200 percent text on primary pages) | Supports |
| 1.4.5 Images of text | AA | No images of text; logos are exempt | Manual | Manual only |
| 1.4.10 Reflow | AA | Single column layouts on phones; tables and charts scroll inside their region | Page check (`interaction.spec.ts` at 320 px; design suite at 390 px) | Supports |
| 1.4.11 Non-text contrast | AA | Focus ring, borders of fields and state indicators meet 3:1 in the tokens | Lint (theme contrast tests); Manual | Supports |
| 1.4.12 Text spacing | AA | Containers grow with text; no fixed heights on text | Page check (`interaction.spec.ts`, WCAG spacing values) | Supports |
| 1.4.13 Content on hover or focus | AA | Tooltips open on hover and focus, close on Escape, stay while hovered (`Tooltip`) | Page check (`specs/components.spec.ts`: shows on focus, Escape hides it without moving focus); Manual (stays while hovered) | Supports |

## Operable

| SC | Level | How TeamBeats meets it | Verification | Status |
|---|---|---|---|---|
| 2.1.1 Keyboard | A | Native controls first; composite widgets (tabs, menus, tree, grid, calendar) have arrow key support | Page check (`interaction.spec.ts`, keyboard journeys in feature suites); Manual | Partial: drag-only moves remain in some editors until T014 |
| 2.1.2 No keyboard trap | A | Overlays trap focus only while open and release on Escape | Page check (`overlays.spec.ts`: Tab stays inside while open, Escape closes and returns focus) | Supports |
| 2.1.4 Character key shortcuts | A | No single character shortcuts; the command palette opens with a modifier | Manual | Manual only |
| 2.2.1 Timing adjustable | A | No time limits on tasks; an idle session ends on the server per the tenant session policy (spec 003), and the shared kiosk signs out after inactivity (spec 030), which is a security exception | Manual | Partial: no warning before an idle session ends |
| 2.2.2 Pause, stop, hide | A | Carousels do not auto-advance; reduced motion stops animation | Page check (A11Y-015, T010) | Supports |
| 2.3.1 Three flashes or below threshold | A | No flashing content | Manual | Manual only |
| 2.4.1 Bypass blocks | A | Skip link to `main` on every page; landmarks | Gate (`bypass`, `region`); Page check (A11Y-009, T009) | Supports |
| 2.4.2 Page titled | A | Every page sets a title from its header | Gate (`document-title`) | Supports |
| 2.4.3 Focus order | A | DOM order; focus moves into dialogs and back to the trigger | Page check (`interaction.spec.ts` Tab walk; `overlays.spec.ts` dialog, drawer and assistant panel); Manual | Supports |
| 2.4.4 Link purpose (in context) | A | Link text names its target; repeated links carry the record name (`aria-label` starting with the visible text) | Gate (`link-name`, `label-content-name-mismatch`) | Supports |
| 2.4.5 Multiple ways | AA | Navigation, search, command palette and quick navigation (spec 008) | Manual | Manual only |
| 2.4.6 Headings and labels | AA | One `h1` per page, ordered headings, visible labels | Gate (`heading-order`, `empty-heading`); Page check (design suite) | Supports |
| 2.4.7 Focus visible | AA | Global `:focus-visible` ring in `color.focus.ring`, never removed | Page check (`interaction.spec.ts` Tab walk) | Supports |
| 2.4.11 Focus not obscured (minimum) | AA | Scroll margins for sticky header and bottom navigation | Page check (`interaction.spec.ts` Tab walk) | Supports |
| 2.5.1 Pointer gestures | A | No multi-point or path gestures needed; swipe has buttons | Manual | Manual only |
| 2.5.2 Pointer cancellation | A | Actions run on click (up event), native buttons | Manual | Manual only |
| 2.5.3 Label in name | A | Accessible names start with the visible text | Gate (`label-content-name-mismatch`) | Supports |
| 2.5.4 Motion actuation | A | No device motion input | | Not applicable |
| 2.5.7 Dragging movements | AA | Move menus and buttons beside every drag (A11Y-019) | Page check (T014) | Partial until T014 is built |
| 2.5.8 Target size (minimum) | AA | Buttons 44 px, compact 28 px, nothing under 24 px (DLS-033, DLS-085) | Page check (`interaction.spec.ts` on primary pages; design suite galleries) | Supports |

## Understandable

| SC | Level | How TeamBeats meets it | Verification | Status |
|---|---|---|---|---|
| 3.1.1 Language of page | A | `lang` from the resolved locale (spec 007) | Gate (`html-has-lang`, `html-lang-valid`) | Supports |
| 3.1.2 Language of parts | AA | Content variants and mixed fragments carry their own `lang` and `dir` (spec 007 US2) | Gate (`valid-lang`); Manual | Partial: user names in another script are isolated (`bdi`) but not language tagged |
| 3.2.1 On focus | A | Focus never changes context | Manual | Manual only |
| 3.2.2 On input | A | Changing a field never submits or navigates; filters update results in place with an announcement | Manual | Manual only |
| 3.2.3 Consistent navigation | AA | One shell per design on every page | Page check (design suite) | Supports |
| 3.2.4 Consistent identification | AA | Shared parts and message keys give one name per function | Lint (`check:messages`) | Supports |
| 3.2.6 Consistent help | A | Ask TeamBeats, the account menu and the footer accessibility link in the same place on every page (A11Y-020) | Manual | Manual only |
| 3.3.1 Error identification | A | Errors in text next to the field, `aria-invalid`, summary announced | Gate (`aria-*`); Manual | Supports |
| 3.3.2 Labels or instructions | A | Visible labels and hints on every field (FormField) | Gate (`label`, `select-name`) | Supports |
| 3.3.3 Error suggestion | AA | Validation messages say how to fix (content guidelines, spec 007 FR-029) | Manual | Manual only |
| 3.3.4 Error prevention (legal, financial, data) | AA | Review step or reversal for leave, expenses, letters, data changes, acknowledgements | Manual | Manual only |
| 3.3.7 Redundant entry | A | Forms prefill from the profile and HR record (spec 019); journeys carry answers forward | Manual | Manual only |
| 3.3.8 Accessible authentication (minimum) | AA | Sign-in through the identity provider; no cognitive tests; paste and autofill allowed, codes paste in one go (A11Y-022). The shared kiosk turns autofill off on purpose and offers a badge scan instead of typing | Page check (`authentication.spec.ts`); Manual | Supports |

## Robust

| SC | Level | How TeamBeats meets it | Verification | Status |
|---|---|---|---|---|
| 4.1.1 Parsing | A | Obsolete in WCAG 2.2 and always satisfied | | Not applicable |
| 4.1.2 Name, role, value | A | Native elements first; ARIA patterns for composite widgets | Gate (`aria-*`, `button-name`, `nested-interactive`); Lint (`jsx-a11y`, T012) | Supports |
| 4.1.3 Status messages | AA | Toasts and result counts in `aria-live` regions (Toast, live region part) | Manual | Manual only |

## Known issues

Moderate and minor findings from the gate, and gaps from manual review, with the task that fixes them.

| Issue | Criterion | Task |
|---|---|---|
| Drag-only moves in some editors | 2.1.1, 2.5.7 | T014 |
| Uploaded video previews have no caption track unless the uploader provides captions | 1.2.2 | Listed as a limitation in the statement |
| Detail routes checked only by their feature suites, not yet in the matrix | all | T008 |
| Generated PDFs are not tagged | 1.3.1, 4.1.2 for documents | T019 (R3) |
| No external assistive technology audit yet | all | T018 (R3) |
