# Plan: TeamBeats Design Language System

## Summary
Designs are token presets in the `Experience` module's theme engine. A choice token `design.preset` selects the preset, which the resolver inserts between the platform defaults and the first brand layer. A second choice token `layout.navigation` carries the one structural difference (rail, top bar, sidebar). The web app reads both from the resolved theme and sets `data-design` and `data-nav` on `<html>`; everything else stays `--tb-*` custom properties. The brand manager gets a design picker fed by the token schema endpoint, which now lists choices and each design's resolved preview properties.

## Constitution check
- I, II: designs are part of the tenant brand and follow spec 033 isolation; no new data. Pass.
- XXI: all designs in every package; organization choice gated by `experience.org_branding`. Pass.
- XXII: four OFL-1.1 font packages, self-hosted, recorded in ADR-0021. The extended catalogue adds no component, CSS or behavior library. Pass.
- XXIII, XXIV, XXXI: guidelines, gates and the design suite. Pass.
- XXIX: designs are versioned platform configuration validated by the token schema; no free CSS. Pass.
- XXX: no business rules in the frontend; the picker only composes a token change the API validates. Pass.

## Architecture
- `Tokens.cs`: token type `choice`, tokens `design.preset` and `layout.navigation`, `Cairo` added to the approved Latin fonts.
- `DesignPresets.cs`: Atlas, Pulse and Studio token values; Beats is the platform default.
- `ThemeEngine.Resolve`: effective preset by layer order and locks, then preset values as the base.
- `GET /api/v1/experience/tokens`: adds `choices` and `designs` (key, navigation, preview per mode).
- Web: `lib/experience/design.ts`, root layout attributes and font imports, shell navigation styles, `components/ui/designs.css`, new components, gallery, design picker, `scripts/check-design.mjs`.
- Tests: `DesignPresetTests`, regenerated `tokens.css`, `design-system.spec.ts` with visual baselines.

## Extended component catalogue (DLS-030 to DLS-041)
The catalogue was drawn up from a study of the BoardUI library (React, Tailwind CSS, React Aria Components, Remix icons; free tier MIT, the rest paid). TeamBeats takes its patterns and finish, not its code: the design system allows no CSS framework and no component library, so each component is written in our React with plain CSS on `--tb-*` tokens, our icon set and our messages.

| BoardUI method | TeamBeats equivalent |
|---|---|
| Tailwind utility classes | Classes in `components/ui/ui.css` with logical properties |
| About 400 semantic color tokens and an accent ramp | The spec 033 tokens; hover and pressed steps come from the state opacity tokens |
| React Aria behavior | Native elements first, shared hooks in `components/ui/hooks` for roving focus, type ahead, dismissal and focus return |
| Remix icons | `Icon.tsx`, extended per phase |
| TanStack Table, Recharts, Motion | Our own table state, SVG charts and CSS transitions with the motion tokens |
| The BoardUI look | The Studio design and its few rules in `designs.css` |

Delivery is eight pull requests: Studio and the specs; actions, selection and status; overlays and menus; inputs and dates; data blocks; assistant and agent parts; charts (with the chart tokens in the schema and in all four designs); page patterns. Each updates the guideline page, the gallery, the keyboard tests and the visual baselines. Existing pages adopt the components as they are touched.

- Sizes follow our floor, not BoardUI's: 44 px main actions, 24 px minimum anywhere (WCAG 2.5.8).
- A gallery page per group (`/ui-gallery/components`) keeps the first gallery page and its baselines stable.
- The lint keeps refusing color functions; shaded fills are layers at the state opacities.

## Risks
- Hand-built comboboxes, menus and calendars can miss keyboard or screen reader cases that a library covers. Mitigation: native elements carry most behavior, each component follows its WAI-ARIA pattern and has a keyboard test and axe run in both directions.
- Visual baselines can differ between machines. Mitigation: self-hosted fonts, a fixed viewport, 2 percent tolerance, Chromium version pinned with the suite.
- Tenants with existing accent overrides keep them on top of a new design. Mitigation: the picker replaces token values on purpose and says so.
