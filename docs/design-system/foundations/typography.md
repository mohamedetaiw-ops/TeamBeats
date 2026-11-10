# Typography

Type carries most of the intranet: news, policies, forms, names. It must be legible in Arabic and English, at every size people set, on every device.

## Typefaces

| Design | Text | Headings | Why |
|---|---|---|---|
| Beats | Readex Pro | Alexandria | One family covers Arabic and Latin, so a bilingual page keeps one voice |
| Atlas | IBM Plex Sans (Latin), IBM Plex Sans Arabic | Same | Neutral, highly legible, common in government and finance |
| Pulse | Cairo | Cairo | Strong Arabic forms with a matching Latin, expressive at large sizes |

- Fonts are self-hosted from the `@fontsource` packages (ADR-0019, ADR-0021), split per script with `unicode-range`. A page downloads only the fonts and scripts it uses.
- Every family has a fallback stack per script (`TokenSchema.FontFallbacks`) so a missing file never leaves missing glyphs.
- Urdu uses Noto Nastaliq Urdu; Hindi uses Noto Sans Devanagari, in every design.
- Never set a font in a component. Use `var(--tb-font-family-body)` and `var(--tb-font-family-heading)`; they switch with the page language.

## Type scale

| Token | Beats | Atlas | Pulse | Use |
|---|---|---|---|---|
| `font.size.xxl` | 32 | 28 | 40 | Page title (h1) |
| `font.size.xl` | 24 | 22 | 26 | Section title (h2) |
| `font.size.lg` | 18 | 18 | 18 | Card title (h3), lead paragraph |
| `font.size.md` | 16 | 16 | 16 | Body text, inputs, buttons |
| `font.size.sm` | 14 | 14 | 14 | Secondary text, hints, table cells |
| `font.size.xs` | 12 | 12 | 12 | Labels under rail icons, metadata. Never for sentences |

Compact density reduces each step by one or two pixels; body text never goes below 14 px (enforced by the theme check `validation.theme.text_too_small`).

## Rules

- **Body text is 16 px with line height 1.5.** Headings use line height 1.25 (1.15 in Pulse).
- **Line length** stays between 45 and 80 characters for reading text. Use the readable column of the Article template.
- **One h1 per page**, then h2 and h3 in order. Never skip a level to get a smaller size; change the size with tokens, not the level.
- **Weights:** regular 400 for text, medium 500 for labels and active navigation, bold 700 (600 in Atlas) for headings and key numbers. No light weights below 400; they fail on low-quality screens and in Arabic.
- **No all caps.** Arabic has no capitals and English all caps is harder to read. No letter spacing on Arabic.
- **No italics in Arabic.** Use weight or color for emphasis.
- **Numbers:** Western Arabic digits (0 to 9) by default in both languages, per spec 007; tenants can switch Arabic pages to Eastern Arabic digits. Use tabular figures in tables.
- **Text resizing:** pages work at 200 percent text size (personal setting and browser zoom) without loss of content (WCAG 1.4.4, 1.4.10).
- **Mixed direction:** wrap names, emails and codes in `<bdi>` so an English name inside Arabic text keeps its order.
