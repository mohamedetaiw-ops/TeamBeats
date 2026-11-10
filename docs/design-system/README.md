# TeamBeats Design System

The TeamBeats Design System is the single design language for every TeamBeats screen: the employee intranet, the administration area, the page builder and the mobile web. It is modeled on the structure of national design systems such as the UAE Design System: **design guidelines** that tell designers what good looks like, and **technical guidelines** that tell developers how to build it so it stays secure, fast and consistent.

It is binding. Constitution principle XXXI (One design language) and spec [036](../../specs/036-design-language-system/spec.md) make it the authority for every user interface. A screen that does not follow it is not done.

## Principles

1. **Clear before clever.** People come to the intranet to get something done. Every screen leads with the task, uses plain words and shows one main action.
2. **Arabic and English are equal.** Every layout, type choice, icon and image works right to left and left to right from the first sketch. Arabic is never a translation pass at the end.
3. **Accessible to everyone.** WCAG 2.2 AA is the floor, not the goal. People of determination use TeamBeats every day with keyboards, screen readers, zoom and switch devices.
4. **Brand is data.** Colors, type, shape and navigation come from tokens that a tenant chooses in administration. Code never hard-codes a look.
5. **One system, four designs.** TeamBeats ships four complete designs (Beats, Atlas, Pulse, Studio). They share every component, rule and test; only the token values and the navigation style differ.
6. **Calm and trustworthy.** Motion, color and imagery support the content and never compete with it. The same thing always looks and behaves the same way.

## What is in it

| Part | For | Pages |
|---|---|---|
| Designs | Everyone | [The four designs](designs.md) |
| Design guidelines | Designers, UX writers, researchers, graphics team | [Typography](foundations/typography.md), [Colour](foundations/colour.md), [Icons](foundations/icons.md), [Imagery](foundations/imagery.md), [Content](foundations/content.md), [Layout](foundations/layout.md), [Actions](foundations/actions.md), [Accessibility](foundations/accessibility.md), [Details](foundations/details.md) |
| Components and templates | Designers and developers | [Components](components.md), [Templates](templates.md) |
| Technical guidelines | Frontend developers | [Frontend development](technical/frontend.md), [Security](technical/security.md), [Performance](technical/performance.md), [Consistency](technical/consistency.md) |
| Testing | Designers, developers, QA | [UI testing strategy](testing.md) |

## Where it lives in the code

| Thing | Place |
|---|---|
| Token schema and the four designs | `src/Modules/Experience/Themes/Tokens.cs`, `DesignPresets.cs` |
| Theme resolution, contrast checks, CSS | `src/Modules/Experience/Themes/ThemeEngine.cs` |
| Generated platform tokens for the web app | `src/Web/app/tokens.css` (never edit by hand) |
| Shared components | `src/Web/components/ui/` |
| Per-design structural rules | `src/Web/components/ui/designs.css` |
| Shell and navigation styles | `src/Web/features/shell/` |
| Living reference (all components in the active design) | `/ui-gallery` in development and staging |
| Design picker for administrators | Administration, Brand (`/admin/branding`) |
| Automated checks | `npm run verify` in `src/Web`, `tests/Acceptance/specs/design-system.spec.ts`, `tests/Unit/DesignPresetTests.cs` |

## How to use it

- **Designers** start every screen from the components and templates here, in the design the customer chose, and check it in Arabic and English, light and dark, at phone, tablet and desktop widths before handing it over. See [testing](testing.md) for the design review checklist.
- **Developers** build only with the shared components and the `--tb-*` tokens. The build fails on raw colors, raw sizes, literal strings, inline icons and images without alternative text.
- **Product owners** reference this system in every spec's UX section instead of describing visuals from scratch.

## Changing the system

The design system changes like code: through a pull request that updates the guideline page, the component, its gallery entry and its tests together. A new component, token or design needs a line in spec 036. A change to the designs that alters what a tenant sees needs a note in the release notes, because every tenant on that design sees it.
