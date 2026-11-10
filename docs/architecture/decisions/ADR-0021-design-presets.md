# ADR-0021: Design presets as token layers, with self-hosted fonts per design

| Field | Value |
|---|---|
| Status | Accepted (2026-11-09, under Mohamed's standing approval to proceed) |
| Date | 2026-11-09 |
| Owners | Mohamed |
| Related specs | 036 (design language system), 033 (FR-003, FR-004, FR-007, FR-029 to FR-031), 008; constitution XXII, XXIX, XXX, XXXI; ADR-0012, ADR-0014, ADR-0019 |

## Context and constraints
Mohamed asked for three totally different designs that customers choose in administration, with the current v2 look as the original build. Spec 033 already makes brand a set of validated semantic tokens resolved per scope, and forbids free CSS. A design differs from a brand colour change: it changes palette, type, shape, elevation and where the navigation sits. Customer-specific code is not allowed (constitution XX, XXIX).

## Options considered
1. **Three separate stylesheets or component sets per design.** Maximum freedom, but three times the components to test, drift between them, and a look that cannot be adjusted by tenants through tokens. Rejected.
2. **Designs as brand templates copied into the tenant brand.** Simple, but a later improvement to a design never reaches tenants who picked it, and the brand editor fills with values the tenant never chose. Rejected.
3. **Designs as a resolver layer selected by a token (chosen).** A `design.preset` choice token selects a fixed token set that the resolver applies between the platform defaults and the tenant brand. One structural choice (`layout.navigation`) is also a token. The web app turns the two choices into `data-design` and `data-nav` attributes; everything else stays custom properties.

## Decision
Option 3.
- New token type `choice` with an allowed list. Tokens `design.preset` (`beats`, `atlas`, `pulse`) and `layout.navigation` (`rail`, `top`, `sidebar`).
- `DesignPresets.cs` holds the values; Beats is the platform default and adds nothing. Each design must pass every contrast pair in strict mode in light and dark (unit tests).
- Resolution: platform, then the effective design, then tenant brand, organizations, user choices; locks win. The design itself is resolved with the same layer order and locks.
- Structural CSS per design is limited to `components/ui/designs.css` and the navigation styles in `features/shell/shell.css`, enforced by the design gate.
- Fonts: add `@fontsource/ibm-plex-sans`, `@fontsource/ibm-plex-sans-arabic` and `@fontsource/cairo` at 5.3.0 (SIL OFL 1.1, already on the CI allow list), weights 400 and 600 for Plex, 400 and 700 for Cairo. Add Cairo to the approved Latin fonts. Only the `@font-face` rules ship to every page; the browser downloads a file only when the active design uses it for characters on the page.

## Consequences
- An improvement to a design reaches every tenant on it at once; release notes must mention visible changes.
- Tenants can start from a design and adjust any token on top; their values win.
- Each page carries about 6 KB more CSS for the extra `@font-face` rules, and no extra font downloads unless the design is in use.
- A fourth design is a code change reviewed against spec 036, never a per-customer setting.
