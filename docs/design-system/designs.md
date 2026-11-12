# The four designs

TeamBeats ships four complete designs. A tenant administrator picks one in **Administration, Brand**. The original build and every new tenant use **Beats**. All four use the same components, templates, rules and tests; they differ in token values and in one structural choice, the navigation style. Each one passes every contrast and text size check in light and dark mode with no finding (`tests/Unit/DesignPresetTests.cs`).

| | Beats (default) | Atlas | Pulse | Studio |
|---|---|---|---|---|
| Personality | Warm, modern, friendly | Formal, institutional, calm | Bold, editorial, energetic | Clean, neutral, product like |
| Suits | Most companies, the TeamBeats look | Government, banks, regulated sectors | Creative, retail, young workforces | Technology companies, operations, data heavy workspaces |
| Navigation | Icon rail on the start edge | Top bar of labelled links under the header | Dark labelled sidebar on the start edge | Light labelled sidebar on the start edge |
| Header | White bar | Deep green bar with a gold mark | White bar with a soft shadow | White bar with a hairline |
| Canvas | Cool grey `#F7F8FB` | Paper white `#F6F7F5` | Warm cream `#FFF8F3` | Neutral grey `#F7F7F7` |
| Accent | Indigo `#4338CA` | Green `#0B6E4F` | Raspberry `#C2185B` | Blue `#155DFC` |
| Highlight | Saffron | Gold | Coral | Teal, with lime on dark surfaces |
| Type | Readex Pro text, Alexandria headings | IBM Plex Sans and IBM Plex Sans Arabic | Cairo for text and headings | Inter, with IBM Plex Sans Arabic for Arabic |
| Headings | 32 / 24 px, bold 700 | 28 / 22 px, semibold 600, gold rule under the page title | 40 / 26 px, bold 700, tight | 30 / 22 px, semibold 600, slightly tight |
| Corners | Controlled (8, 12, 16 px) | Square (2, 4, 6 px) | Round (10, 18, 28 px), pill buttons | Even (8, 10, 20 px) |
| Elevation | Light shadows | Borders, almost no shadow | Deep soft shadows | Hairline borders, very soft shadows, shaded filled buttons |

The token values are in `src/Modules/Experience/Themes/DesignPresets.cs`; Beats is the platform default in `Tokens.cs`. Dark mode values are defined for every color in all four designs.

Studio recreates the look of modern product dashboards (it was modeled on the BoardUI component library): one accent, neutral surfaces and a careful finish. Its filled buttons and current sidebar page carry a soft shade and a light top edge, drawn in `components/ui/designs.css` from the design's own tokens. Its accent is dark enough for white text to pass the strict contrast check. The finish reaches every page (DLS-075): cards, tiles, stat cards, filter bars and row lists are white with a hairline and the soft shadow, section tabs are a segmented track with a raised white current page, icon boxes are white with a hairline, and the home briefing is a light card with bordered rows instead of the night surface.

## How a design is applied

1. The brand profile stores the token `design.preset` with `beats`, `atlas`, `pulse` or `studio`.
2. The theme resolver (spec 033 FR-004) applies, in order: platform defaults, the design's values, then the tenant brand, then each organization from the root down, then the user's allowed choices (mode, density, text size). Locks win at every step.
3. So a tenant can pick Atlas and still change its accent color to its own brand color; the contrast checks run on the result.
4. The resolved `design.preset` and `layout.navigation` values reach the page as `data-design` and `data-nav` attributes on `<html>`. Everything else arrives as `--tb-*` custom properties.

## Choosing a design (administrators)

- Open **Administration, Brand**. The **Design** section shows the four designs as cards with a live miniature in their own colors, type, shape and navigation.
- Choose **Use Atlas** (for example). The editor replaces the token values changed so far with the design; the brand name, logo and other images stay. The contrast table and the live preview update straight away.
- Nothing changes for employees until **Save and publish**. Saving creates a new brand version, so the previous look can be restored from history, and the change is audited (`theme.published`).
- Organizations with organization branding (`experience.org_branding`) can choose a different design for their subtree unless the tenant locked `design.preset`.
- Every package includes all four designs. Choosing a design needs `theme.edit` and `theme.publish`, like any brand change.

## Rules for the designs

- A design is a full, accessible look. It MUST define every color in light and dark and pass every contrast pair of spec 033 FR-009 in strict mode.
- A design MUST NOT change behavior, information architecture, permissions, content or component order. It changes how things look and where the navigation sits, nothing else.
- Structural differences that a token value cannot express live only in `src/Web/components/ui/designs.css` and the navigation styles in `features/shell/shell.css`. Keep them few.
- Fonts come only from the approved list (ADR-0019, ADR-0021), self-hosted.
- Adding another design is a change to spec 036 and ADR-0021, a new entry in `DesignPresets.cs`, a new miniature label, screenshots in the design review and visual baselines.
