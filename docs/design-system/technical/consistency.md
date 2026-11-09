# Consistency

Consistency makes TeamBeats look professional and trustworthy, and it is what lets one code line serve three designs and hundreds of tenant brands. Most of it is enforced by the build.

## Automated gates (fail the build)

| Gate | Command | What it refuses |
|---|---|---|
| TypeScript | `npm run typecheck` | Missing required props such as `alt`, `label`, `title` |
| ESLint | `npm run lint` | Literal user-facing strings in JSX, React and Next.js rule violations |
| stylelint | `npm run lint:css` | Raw colours, pixel values, `left`/`right` properties and values |
| Messages | `npm run check:messages` | Keys missing in Arabic or English, unknown keys in code |
| Placements | `npm run check:placements` | Placements importing each other |
| Token gate | `npm run check:tokens` | Custom properties not in the token schema, setting `--tb-*` in CSS |
| Design gate | `npm run check:design` | Inline `<svg>` outside the icon set, `<img>` without `alt`, literal font families or sizes, per-design selectors outside `designs.css` |
| Token file | `dotnet test tests/Unit` | `tokens.css` out of date with the schema; designs that fail contrast |
| Design suite | `npx playwright test specs/design-system.spec.ts` | Serious or critical axe violations, horizontal scrolling, targets under 24 px, visual drift of the gallery in any design or direction |

All of them run in CI on every pull request (`web` and `e2e` jobs).

## Review checklist (pull requests that change UI)

- Uses existing components; no new one-off pattern for something the system already has.
- Same words for the same things (see the glossary in `messages/en.json` namespaces, for example "Request", not "Ticket" in one place and "Case" in another).
- Same action in the same place: primary first in the reading direction, Cancel last.
- Same states everywhere: empty, loading, error and denied use `StateView`.
- Screenshots in Beats, Atlas and Pulse attached for visible changes, Arabic and English.
- Visual baselines updated only when the change is intended, and the reviewer looked at the diff.

## Updating visual baselines

When a component's look changes on purpose, regenerate the baselines and commit them with the change:

```
cd tests/Acceptance
npx playwright test specs/design-system.spec.ts --update-snapshots
```

Review every changed image in the pull request. A baseline change nobody looked at defeats the check.
