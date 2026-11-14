# Consistency

Consistency makes TeamBeats look professional and trustworthy, and it is what lets one code line serve four designs and hundreds of tenant brands. Most of it is enforced by the build.

## Automated gates (fail the build)

| Gate | Command | What it refuses |
|---|---|---|
| TypeScript | `npm run typecheck` | Missing required props such as `alt`, `label`, `title` |
| ESLint | `npm run lint` | Literal user-facing strings in JSX, React and Next.js rule violations |
| stylelint | `npm run lint:css` | Raw colours, pixel values, `left`/`right` properties and values |
| Messages | `npm run check:messages` | Keys missing in Arabic or English, unknown keys in code |
| Placements | `npm run check:placements` | Placements importing each other |
| Token gate | `npm run check:tokens` | Custom properties not in the token schema, setting `--tb-*` in CSS |
| Design gate | `npm run check:design` | Inline `<svg>` outside the icon set, `<img>` without `alt`, literal font families or sizes, per-design selectors outside `designs.css`, a page without the shared page header, a feature style sheet that draws its own card, tabs, tag or filter bar, a shared component with no gallery entry |
| Token file | `dotnet test tests/Unit` | `tokens.css` out of date with the schema; designs that fail contrast |
| Design suite | `npx playwright test specs/design-system.spec.ts` | Serious or critical axe violations, horizontal scrolling, targets under 24 px, text under 12 px, a font outside the design, visual drift, on the gallery and on the main pages in every design, direction and density |
| Experience suite | `npx playwright test specs/experience.spec.ts` | A first viewport without the primary level, a summary over its budget, a data source shown twice, a placement without its states |

All of them run in CI on every pull request (`web` and `e2e` jobs).

## Review checklist (pull requests that change UI)

- Uses existing components; no new one-off pattern for something the system already has.
- The page states its three levels and they show in that order; the pattern for each kind of information comes from the composition table.
- Summaries are inside their budget, no data source appears twice, and every state is built.
- Same words for the same things (see the glossary in `messages/en.json` namespaces, for example "Request", not "Ticket" in one place and "Case" in another).
- Same action in the same place: primary first in the reading direction, Cancel last.
- Same states everywhere: empty, loading, error and denied use `StateView`.
- Screenshots in Beats, Atlas, Pulse and Studio attached for visible changes, Arabic and English.
- Visual baselines updated only when the change is intended, and the reviewer looked at the diff.

## Updating visual baselines

When a component's look changes on purpose, regenerate the baselines and commit them with the change:

```
cd tests/Acceptance
npx playwright test specs/design-system.spec.ts --update-snapshots
```

Review every changed image in the pull request. A baseline change nobody looked at defeats the check.
