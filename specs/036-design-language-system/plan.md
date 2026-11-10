# Plan: TeamBeats Design Language System

## Summary
Designs are token presets in the `Experience` module's theme engine. A choice token `design.preset` selects the preset, which the resolver inserts between the platform defaults and the first brand layer. A second choice token `layout.navigation` carries the one structural difference (rail, top bar, sidebar). The web app reads both from the resolved theme and sets `data-design` and `data-nav` on `<html>`; everything else stays `--tb-*` custom properties. The brand manager gets a design picker fed by the token schema endpoint, which now lists choices and each design's resolved preview properties.

## Constitution check
- I, II: designs are part of the tenant brand and follow spec 033 isolation; no new data. Pass.
- XXI: all designs in every package; organization choice gated by `experience.org_branding`. Pass.
- XXII: three new OFL-1.1 font packages, self-hosted, recorded in ADR-0021. Pass.
- XXIII, XXIV, XXXI: guidelines, gates and the design suite. Pass.
- XXIX: designs are versioned platform configuration validated by the token schema; no free CSS. Pass.
- XXX: no business rules in the frontend; the picker only composes a token change the API validates. Pass.

## Architecture
- `Tokens.cs`: token type `choice`, tokens `design.preset` and `layout.navigation`, `Cairo` added to the approved Latin fonts.
- `DesignPresets.cs`: Atlas and Pulse token values; Beats is the platform default.
- `ThemeEngine.Resolve`: effective preset by layer order and locks, then preset values as the base.
- `GET /api/v1/experience/tokens`: adds `choices` and `designs` (key, navigation, preview per mode).
- Web: `lib/experience/design.ts`, root layout attributes and font imports, shell navigation styles, `components/ui/designs.css`, new components, gallery, design picker, `scripts/check-design.mjs`.
- Tests: `DesignPresetTests`, regenerated `tokens.css`, `design-system.spec.ts` with visual baselines.

## Risks
- Visual baselines can differ between machines. Mitigation: self-hosted fonts, a fixed viewport, 2 percent tolerance, Chromium version pinned with the suite.
- Tenants with existing accent overrides keep them on top of a new design. Mitigation: the picker replaces token values on purpose and says so.
