# Frontend development

These guidelines tell developers how to build TeamBeats web UI so that it uses the design system consistently while staying secure, fast and maintainable. They apply to every page, placement and admin screen in `src/Web`.

## Stack

Next.js (App Router) with React server components by default, `next-intl` for messages, plain CSS files per feature with logical properties, and self-hosted fonts. No CSS framework, no component library, no CSS-in-JS: the design system is our component library (constitution XXII on dependencies; ADR-0014).

## Building a screen

1. Start from a template in [templates](../templates.md) and the components in [components](../components.md). Check the gallery (`/ui-gallery`) for the current look in all three designs.
2. Server components fetch data through `lib/*/client.ts` API clients (the backend-for-frontend). Client components only where there is interaction.
3. Every string is a message key in `messages/en.json` and `messages/ar.json`. One `useTranslations` or `getTranslations` namespace per file.
4. Style with the feature's CSS file using only `--tb-*` tokens, logical properties and `rem` for component sizes. Local layout variables use the `--xp-` prefix.
5. Handle every state with `StateView` and `Skeleton`: loading, empty, error, denied, offline.
6. Add or update the e2e test with axe for the page in Arabic and English.

## Tokens

- The token schema is in `src/Modules/Experience/Themes/Tokens.cs`. `src/Web/app/tokens.css` is generated from it by the unit tests (`TEAMBEATS_UPDATE_TOKENS=1`); never edit it by hand.
- Read tokens with `var(--tb-...)`. Never set a `--tb-*` property in CSS; only the tenant theme and the generated file set them.
- Body and heading fonts are `--tb-font-family-body` and `--tb-font-family-heading`; they switch with the page language.
- Design-specific structure uses `html[data-design]` and `html[data-nav]` attributes, only in `components/ui/designs.css` and `features/shell/shell.css`.
- A new token is a change to the schema, the three designs (each must still pass the contrast tests), spec 033 and the brand editor groups.

## Components

- Shared components live in `components/ui`. Feature components live in `features/<feature>` and never import another feature's components (ADR-0014). Placements live in `components/placements` and talk only through declared events.
- Props are typed; required accessibility props (`alt`, `label`, `title`) are required in the type, not optional.
- Components are direction-agnostic: logical properties, `<bdi>` around user data, icons mirrored only through `Icon`.
- No business rules, role checks, package checks or permission logic in React (constitution XXX). Hide or disable for usability only after the API decided.

## Right to left and localization

- `<html lang dir>` is set by the root layout from the user's locale. Use `dir="ltr"` only on content that is always left to right (emails, code, URLs, phone numbers).
- Never concatenate translated strings; use ICU messages with plurals and selects.
- Dates, numbers and times through `Intl` with the user's locale and the tenant's calendar settings (`lib/clock.ts` `useNow` for times that must wait for the browser).

## Definition of done for UI work

- [ ] Built from shared components and tokens; `npm run verify` passes (types, lint, CSS lint, messages, placements, tokens, design gate).
- [ ] Works at 375, 768 and 1440 px, in Arabic and English, light and dark, in Beats, Atlas and Pulse.
- [ ] Keyboard only and screen reader checked on primary journeys; axe has no critical or serious violations.
- [ ] Loading, empty, error and denied states shown.
- [ ] Playwright journey added or updated; visual baseline updated on purpose if the look changed.
- [ ] Designer signed off the post-development review (see [testing](../testing.md)).
