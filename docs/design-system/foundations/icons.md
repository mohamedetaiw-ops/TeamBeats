# Icons

Icons help people scan and recognise; words still do the explaining. TeamBeats uses one outline icon set, drawn in code, so every design and mode restyles it.

## The set

- 24 by 24 grid, 2 px round strokes, no fills, drawn in `currentColor`. Defined once in `src/Web/components/ui/Icon.tsx` (`iconNames` lists them; the gallery shows them all).
- Sizes: 16 px inline with text, 20 px in buttons and alerts, 22 to 24 px in navigation, 24 px in empty states. Never stretch or scale unevenly.
- The four point star (`spark`) is reserved for AI features. Do not use it for anything else.

## Rules

- **Pair icons with text.** An icon on its own needs an accessible name (`<Icon label="...">` or a labelled button) and a tooltip on hover and focus. Prefer a visible label in navigation; Beats shows the label under each rail icon.
- **Decorative by default.** An icon next to a word that says the same thing is hidden from screen readers (the default).
- **Mirror only what points.** Icons that show direction (`chevron-forward`, `arrow-forward`, back arrows, external link arrows when they point along the line) mirror in Arabic. Clocks, checkmarks, search, media play buttons, logos and numbers never mirror. The component mirrors `chevron-forward` and `arrow-forward` automatically.
- **One meaning per icon.** `trash` always deletes, `edit` always edits. Do not reuse an icon for a different action.
- **Contrast.** An icon that carries meaning needs 3:1 against its background (WCAG 1.4.11). Icons inherit the text colour, which already passes.
- **No inline SVG elsewhere.** New glyphs are added to the set (same grid and stroke) through a pull request with a gallery check. The design gate (`npm run check:design`) fails on inline `<svg>` outside the icon set and the shell brand mark.
- **No emoji as interface icons.** Emoji render differently per device and read badly with screen readers.
