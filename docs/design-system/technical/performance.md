# Frontend performance

People open the intranet many times a day, often on modest laptops, phones and busy networks. Pages must feel instant.

## Budgets

| Metric (75th percentile, real users) | Target | Checked by |
|---|---|---|
| Largest Contentful Paint | under 2.5 s | Web Vitals collection (spec 008), `performance.spec.ts` |
| Interaction to Next Paint | under 200 ms | Web Vitals collection |
| Cumulative Layout Shift | under 0.1 | `theme.spec.ts`, `performance.spec.ts` |
| Theme CSS per brand and mode | under 30 KB compressed | `ThemeEngineTests` |
| Fonts on first view | only the scripts and weights the page uses | `unicode-range` subsets (ADR-0019, ADR-0021) |

## Rules

- **Server first.** Render on the server; ship client JavaScript only for interaction. Keep client components small and leaf-level.
- **Theme without flash.** The tenant theme stylesheet is linked in the head before paint, so switching design or mode never shifts layout.
- **Fonts.** Self-hosted with `font-display: swap`; each design loads at most two families and three weights. Font files for designs a tenant does not use are never downloaded (only their `@font-face` rules ship).
- **Images.** Always `Media` with a ratio, lazy below the fold, sized to their display width, priority only for the hero. No images for text.
- **Lists.** Page or virtualize anything that can exceed about 50 items. Search and filters debounce at about 300 ms.
- **Placements fail alone.** A slow or failing component must not block the page (constitution XXX); it shows its own skeleton then its own error.
- **Motion** uses `transform` and `opacity` only, with the motion tokens, and stops for reduced motion.
- **No heavy dependencies** for small features. Measure the bundle impact of any new package in the pull request.
