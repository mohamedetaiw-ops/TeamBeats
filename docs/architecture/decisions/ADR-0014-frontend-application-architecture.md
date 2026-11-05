# ADR-0014: Frontend application architecture

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-05 |
| Owners | Mohamed |
| Related specs | 007, 008, 032, 033; constitution XXIII, XXIV, XXX |

## Context and constraints
ADR-0012 sets the experience engine: versioned page records, a resolver, component manifests, a Next.js renderer and design tokens. It does not say how frontend code itself is organized: state, data fetching, design primitives, how placements talk to each other, and how the builder implements undo. An architecture recommendation shared on 2026-11-05 proposes component-driven architecture, MVVM with unidirectional data flow, a set of behavioral patterns, and Tailwind CSS, Radix UI, TanStack Query and Zustand.

The frame: Next.js 16 App Router with server components, TypeScript strict, `next-intl`, logical CSS properties only with stylelint enforcement (r0-solution-structure section 6), semantic tokens only (spec 033, 032 FR-026), WCAG 2.2 AA, tenant-scoped caches (032 FR-065), and new dependencies only with a reason and a passing licence scan (constitution XXII).

## Decision drivers
Product rules stay on the server (constitution III, XXX), accessibility and RTL by default, components from different teams that cannot break each other, few dependencies.

## Options considered
1. **Plain React and CSS modules, no libraries.** Fewest dependencies. Every accessible menu, dialog, combobox and popover is written and tested by us, which is slow and easy to get wrong.
2. **A full component library (for example MUI).** Fast, but its own theming fights the token contract of spec 033 and its look leaks into white-labeled tenants.
3. **Headless primitives plus token-bound styling, with scoped state libraries (chosen).**

## Decision
Option 3.

1. **Folder boundaries.** `src/Web` is split into `app/` (routes), `features/<feature>/` (one per product area), `components/ui/` (design primitives), `components/placements/` (registered components, one folder each), `lib/api/` (the only API client) and `lib/experience/` (renderer, registry, event channel). A feature or placement MUST NOT import another feature or placement; shared needs move to `components/ui` or `lib`. Enforced with ESLint import boundary rules.
2. **View and view model.** Server components fetch and render by default. Interactive parts are client components whose state and behavior live in a hook per feature (the view model); the component renders what the hook returns. Data flows one way: server data and props down, user intents up as actions. No business rule, role check or package check in either (constitution XXX).
3. **Server state.** Server components for first render. On interactive surfaces (builder, task inbox, notification centre, personalization) TanStack Query (MIT) caches server state; every query key starts with tenant ID and scope, and the cache is cleared on tenant switch and sign-out (032 FR-065).
4. **Client state.** React state and reducers by default. Zustand (MIT) is allowed only for a feature store that several distant components of one feature share, namely the builder document and personalization edit mode. No app-wide store of business data.
5. **Design primitives.** Radix UI primitives (MIT) underpin `components/ui` for dialogs, menus, popovers, tabs, tooltips and similar, wrapped once with our tokens, `DirectionProvider` and resource-key labels. Placements use `components/ui`, never Radix directly.
6. **Styling.** Tailwind CSS (MIT) is allowed with three constraints: the theme maps only to the semantic token CSS variables of spec 033; arbitrary values are disallowed by lint; physical direction utilities (`ml-`, `mr-`, `pl-`, `pr-`, `left-`, `right-`, `text-left`, `text-right`) are disallowed in favour of logical ones (`ms-`, `me-`, `ps-`, `pe-`, `start-`, `end-`, `text-start`). If those lint rules cannot be made reliable, CSS modules with token variables are used instead and Tailwind is dropped.
7. **Placement interaction.** Placements communicate through a page-scoped event channel (observer pattern) using only the events and actions declared in their manifests (032 FR-067). Payloads are validated against the declared schema; undeclared events are dropped and logged.
8. **Behavioral patterns, where they apply:**
   - Composite: page, layout, region, section, placement tree.
   - Registry and factory: placement code resolved from the registry by manifest reference and loaded lazily.
   - Strategy: layout variants, navigation styles, density and presentation modes, chosen from configuration.
   - State machine: builder save states (clean, dirty, saving, conflict, offline), the page and template lifecycle states of spec 032, placement lifecycle (loading, ready, empty, error, unavailable). States and transitions are typed unions with a reducer; no state machine library is added.
   - Command: every builder and personal layout operation is a command with `apply` and `revert`, kept on a stack, giving undo and redo (032 FR-043, 008 FR-027) and an operation log for autosave.
   - Decorator: the renderer wraps each placement with an error boundary, size-reserving suspense placeholder, telemetry and visibility rule. A visibility wrapper is targeting, never access.
   - Adapter: each placement reads data through a typed client for the owning module's endpoints declared in its manifest data contract.
9. **Contract tests.** The two unrelated reference component packs of spec 032 run in CI against every template, together with axe checks in `ar` and `en`.

## Consequences and risks
- Four new runtime dependencies (Radix UI, Tailwind CSS, TanStack Query, Zustand), all MIT, each with the reason above. Each passes the licence scan and SBOM.
- Tailwind's appeal is speed; its risk is bypassing tokens and logical properties. The lint constraints are the price of using it, with CSS modules as the fallback.
- View model hooks add a file per interactive feature, in exchange for UI logic that can be unit tested without rendering.

## Security and multi-tenancy implications
The browser holds no tokens (session cookie only, r0-solution-structure section 5). Query caches are tenant and scope keyed and cleared on tenant switch. Event channel payloads never carry data a receiving placement could not fetch itself under the same session. Hiding or disabling controls is presentation only; the API decides.

## Rollout and migration plan
R1, with the first Experience module work: folder boundaries and lint rules, `components/ui` on Radix, Tailwind decision confirmed by a spike on the lint rules, TanStack Query on the builder and task inbox, event channel and command stack in the builder. The R0 admin shell and sign-in pages move into `features/` when next changed.
