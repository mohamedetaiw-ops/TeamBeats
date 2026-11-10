# Colour

Colour tells people where they are, what they can do and what needs attention. In TeamBeats every colour is a semantic token; the design and the tenant brand decide the value.

## Semantic tokens

| Group | Tokens | Meaning |
|---|---|---|
| Surfaces | `color.surface.base`, `.raised`, `.sunken` | Page canvas, cards and panels, wells and code |
| Shell | `color.shell.background`, `color.shell.text` | The header bar |
| Navigation | `color.nav.background`, `.text`, `.active` | Rail, top bar or sidebar |
| Text | `color.text.primary`, `.secondary`, `.inverse` | Body text, supporting text, text on accent |
| Accent | `color.accent.base`, `.text`, `.subtle` | Primary actions, selection, brand moments |
| Highlight | `color.highlight.base` | Secondary brand colour for small accents |
| Links | `color.link` | Inline and standalone links |
| Borders | `color.border.default`, `.strong` | Dividers and card edges; input and control edges |
| States | `color.state.success`, `.warning`, `.danger`, `.info` | Status, alerts, validation |
| Focus | `color.focus.ring`, `.ring_inverse` | Keyboard focus on light and on shell surfaces |
| Tints | `color.tint.warm`, `.fresh`, `.cool` and their `_text` | Soft tiles for counts and icons (Your day, service tiles, avatars). Never for status on their own |
| Night | `color.surface.night`, `color.text.on_night`, `color.highlight.bright` | The briefing hero and other personal or AI summaries; dark in both modes |
| Campaign | `color.surface.campaign` | The campaign band at the bottom of the home, with `color.text.on_night` text |

The palette of each design is in [designs](../designs.md). Components use token names, never values; the build refuses raw colors (`stylelint color-no-hex` and the token gate).

## Contrast (enforced)

The theme engine checks every pair below in light and dark for every design and every tenant brand (spec 033 FR-009). Shell and navigation failures block saving; accent failures warn unless the tenant chooses strict mode. The three designs pass all of them in strict mode.

| Pair | Minimum |
|---|---|
| Text primary and secondary on surfaces; shell text; navigation text | 4.5:1 |
| Accent text on accent; links on the canvas | 4.5:1 |
| Active navigation, strong borders, state colours, accent on canvas | 3:1 |
| Focus ring on canvas, shell and navigation | 3:1 |
| Inverse text on accent (large text) | 3:1 |
| Tint text on its tint; on-night text on night and campaign; night on bright highlight | 4.5:1 |

## Rules

- **Never use colour alone** to carry meaning. Status badges have words, errors have text and an icon, charts have labels or patterns (WCAG 1.4.1).
- **One accent per view.** The accent marks the primary action and the current place. Do not paint whole sections in accent; use `accent.subtle` for large tinted areas.
- **State colours are for state.** Green means done or success, amber means attention, red means error or destructive, blue means information. Do not use them for decoration or categories.
- **Dark mode is designed, not inverted.** Every design defines its dark values; check every screen in both modes.
- **High contrast and forced colours:** components use real borders and outlines, not only shadows or backgrounds, so they survive Windows high contrast mode.
- **Charts** use the categorical palette of the data visualization guidelines, built from the design's accent and state colours, and always have a table alternative.
