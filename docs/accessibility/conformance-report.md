# TeamBeats WCAG 2.2 conformance report

Spec 038 A11Y-002. This report follows the layout of the ITI Accessibility Conformance Report (VPAT) for WCAG 2.2. It is derived from the conformance map (`wcag-2.2-conformance.md`) and is never more favorable than it: when the two differ, the map wins and this report is corrected.

- **Product**: TeamBeats web app and installable web app (spec 030), release R2.
- **Report date**: 2026-11-18.
- **Standard**: WCAG 2.2, levels A and AA.
- **Scope**: every route in `tests/Acceptance/a11y/routes.ts`, in the Beats, Atlas, Pulse and Studio designs, light and dark, Arabic (right to left) and English, desktop and phone width.
- **Evaluation methods**: automated axe checks on every pull request (zero critical and serious findings allowed), real-page keyboard, focus, reflow, text size, text spacing, target size, motion, overlay and authentication checks, build-time lint and token contrast checks, and internal keyboard and screen reader review.
- **Assistive technology testing**: internal only. An external audit with NVDA, JAWS, VoiceOver and TalkBack in Arabic and English is planned before the R3 release (A11Y-024), and this report will be updated from its findings.

**Conformance levels used**: Supports (meets the criterion without known defects), Partially supports (some functionality does not meet it), Does not support, Not applicable.

## Level A

| Criterion | Conformance level | Remarks |
|---|---|---|
| 1.1.1 Non-text content | Supports |  |
| 1.2.1 Audio-only and video-only (prerecorded) | Supports | Met by design; verified by internal review only until the external audit. |
| 1.2.2 Captions (prerecorded) | Partially supports | Caption track not yet required on the document preview player (`DocumentPanel`). |
| 1.2.3 Audio description or media alternative | Supports | Met by design; verified by internal review only until the external audit. |
| 1.3.1 Info and relationships | Supports |  |
| 1.3.2 Meaningful sequence | Supports |  |
| 1.3.3 Sensory characteristics | Supports | Met by design; verified by internal review only until the external audit. |
| 1.4.1 Use of color | Supports |  |
| 1.4.2 Audio control | Supports | Met by design; verified by internal review only until the external audit. |
| 2.1.1 Keyboard | Supports |  |
| 2.1.2 No keyboard trap | Supports |  |
| 2.1.4 Character key shortcuts | Supports | Met by design; verified by internal review only until the external audit. |
| 2.2.1 Timing adjustable | Partially supports | No warning before an idle session ends. |
| 2.2.2 Pause, stop, hide | Supports |  |
| 2.3.1 Three flashes or below threshold | Supports | Met by design; verified by internal review only until the external audit. |
| 2.4.1 Bypass blocks | Supports |  |
| 2.4.2 Page titled | Supports |  |
| 2.4.3 Focus order | Supports |  |
| 2.4.4 Link purpose (in context) | Supports |  |
| 2.5.1 Pointer gestures | Supports | Met by design; verified by internal review only until the external audit. |
| 2.5.2 Pointer cancellation | Supports | Met by design; verified by internal review only until the external audit. |
| 2.5.3 Label in name | Supports |  |
| 2.5.4 Motion actuation | Not applicable | No device motion input |
| 3.1.1 Language of page | Supports |  |
| 3.2.1 On focus | Supports | Met by design; verified by internal review only until the external audit. |
| 3.2.2 On input | Supports | Met by design; verified by internal review only until the external audit. |
| 3.2.6 Consistent help | Supports | Met by design; verified by internal review only until the external audit. |
| 3.3.1 Error identification | Supports |  |
| 3.3.2 Labels or instructions | Supports |  |
| 3.3.7 Redundant entry | Supports | Met by design; verified by internal review only until the external audit. |
| 4.1.1 Parsing | Not applicable | Obsolete in WCAG 2.2 and always satisfied |
| 4.1.2 Name, role, value | Supports |  |

## Level AA

| Criterion | Conformance level | Remarks |
|---|---|---|
| 1.2.4 Captions (live) | Not applicable | No live media in the product |
| 1.2.5 Audio description (prerecorded) | Supports | Met by design; verified by internal review only until the external audit. |
| 1.3.4 Orientation | Supports | Met by design; verified by internal review only until the external audit. |
| 1.3.5 Identify input purpose | Supports |  |
| 1.4.3 Contrast (minimum) | Supports |  |
| 1.4.4 Resize text | Supports |  |
| 1.4.5 Images of text | Supports | Met by design; verified by internal review only until the external audit. |
| 1.4.10 Reflow | Supports |  |
| 1.4.11 Non-text contrast | Supports |  |
| 1.4.12 Text spacing | Supports |  |
| 1.4.13 Content on hover or focus | Supports |  |
| 2.4.5 Multiple ways | Supports | Met by design; verified by internal review only until the external audit. |
| 2.4.6 Headings and labels | Supports |  |
| 2.4.7 Focus visible | Supports |  |
| 2.4.11 Focus not obscured (minimum) | Supports |  |
| 2.5.7 Dragging movements | Supports |  |
| 2.5.8 Target size (minimum) | Supports |  |
| 3.1.2 Language of parts | Partially supports | User names in another script are isolated (`bdi`) but not language tagged. |
| 3.2.3 Consistent navigation | Supports |  |
| 3.2.4 Consistent identification | Supports |  |
| 3.3.3 Error suggestion | Supports | Met by design; verified by internal review only until the external audit. |
| 3.3.4 Error prevention (legal, financial, data) | Supports | Met by design; verified by internal review only until the external audit. |
| 3.3.8 Accessible authentication (minimum) | Supports |  |
| 4.1.3 Status messages | Supports | Met by design; verified by internal review only until the external audit. |

## Known limitations

The known issues table of the conformance map lists each gap and the task that closes it. The public statement at `/accessibility` lists the same limitations in plain language.
