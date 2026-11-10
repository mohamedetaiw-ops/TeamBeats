# Accessibility

TeamBeats is used by everyone in an organization, including people of determination who use screen readers, magnification, voice control, switch devices or keyboards only. Accessibility is a legal duty in many of the countries we serve and a basic condition of a fair workplace. The target is **WCAG 2.2 level AA** for every screen in Arabic and English (constitution XXIV, spec 007).

## What every screen must do

| Area | Requirement | WCAG |
|---|---|---|
| Structure | One h1, headings in order, landmarks (header, nav, main), lists as lists, tables with headers | 1.3.1, 2.4.6 |
| Text alternatives | Meaningful alt text on images, labels on icon buttons, captions and transcripts on video | 1.1.1, 1.2.2 |
| Colour | Text 4.5:1, large text and UI parts 3:1, never colour alone | 1.4.1, 1.4.3, 1.4.11 |
| Resize and reflow | Works at 200 percent text and at 320 px wide with no horizontal scrolling | 1.4.4, 1.4.10 |
| Text spacing | Survives increased line, letter and word spacing | 1.4.12 |
| Keyboard | Everything works with keyboard only, logical tab order, no traps, Escape closes popups | 2.1.1, 2.1.2, 2.4.3 |
| Focus | Visible focus ring of at least 2 px in `color.focus.ring`, never hidden behind sticky headers | 2.4.7, 2.4.11 |
| Targets | At least 24 by 24 px; TeamBeats uses 44 by 44 px for buttons and menu items | 2.5.8 |
| Motion | Respects reduced motion (device or personal setting); nothing flashes more than three times a second | 2.3.1, 2.3.3 |
| Forms | Visible labels, hints linked to fields, errors in text next to the field and announced, input kept on error | 3.3.1, 3.3.2, 3.3.3 |
| Consistent help and navigation | Same navigation and help in the same place on every page | 3.2.3, 3.2.6 |
| Redundant entry and authentication | Do not ask for the same information twice; no cognitive tests to sign in | 3.3.7, 3.3.8 |
| Language | `lang` and `dir` set per page and per mixed-language fragment | 3.1.1, 3.1.2 |
| Status messages | Results of actions announced without moving focus | 4.1.3 |

## Arabic and right to left

- Screen readers must read Arabic pages with an Arabic voice: the page sets `lang="ar"` and `dir="rtl"`, and English fragments are wrapped with their own `lang`.
- Arrow key behaviour in tabs, menus and sliders follows the reading direction (see `<Tabs>`).
- Test with NVDA and JAWS in Arabic on Windows, VoiceOver on macOS and iOS, and TalkBack on Android.

## Designers

- Annotate designs with heading levels, landmarks, focus order, alt text and accessible names before handover.
- Check contrast of every new colour pair; the brand editor shows each pair's ratio in text.
- Design focus, hover, error, disabled, loading and empty states for every component, in both directions.

## Developers

- Use the shared components; they carry the semantics. Native elements first, ARIA only when no native element fits.
- Never remove focus outlines. Never use `tabindex` above 0.
- Run `npm run verify` and the Playwright suite: axe runs on every primary page in both languages; the design suite requires zero serious and zero critical violations in all three designs.

## Verified by

Automated: axe in Playwright (`expectAccessible`, design suite), target size and reflow checks, theme contrast checks in the engine. Manual: keyboard and screen reader pass on every primary journey in the Definition of Done, and a usability session with people of determination before each release (see [testing](../testing.md)).
