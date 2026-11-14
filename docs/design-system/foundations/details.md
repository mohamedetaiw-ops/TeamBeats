# Details

The layouts of a screen can be right and the screen can still look generated. What gives it away is the detail: raw values, browser default controls, everything at the same weight, and space nobody designed. This page is the checklist every screen passes before it merges (spec 036 DLS-042 to DLS-068). The reference boards are in the design canvas "TeamBeats component redesign".

## Geometry

| Thing | Value | Token |
|---|---|---|
| Page card, table | 16px radius, 1px border, hairline shadow | `radius.lg`, `color.border.default`, `shadow.sm` |
| Tile, inner card | 12px radius | `radius.md` |
| Input, select | 8px radius, 40px high (36 compact) | `radius.sm` |
| Button | 8px radius; medium 44px, small 36px, compact 28px (DLS-033) | `radius.sm` |
| Popover, sheet, dialog | 12px radius, strong shadow | `radius.md`, `shadow.md` |
| Badge, chip | 6px radius, 22 to 24px high | `radius.sm` less 2px |
| Fully round | Avatar, count bubble, assistant launcher only | `radius.full` |
| Spacing | 4, 8, 12, 16, 24, 32, 48 | `space.*` |

## Type

| Role | Size | Weight |
|---|---|---|
| Page title | 28 to 32px | semibold |
| Section heading | 18 to 20px | semibold |
| Card and row title | 14 to 16px | semibold |
| App body | 14px | regular |
| Reading text | 16px, line height 1.6 | regular |
| Meta | 12 to 13px, secondary text colour | regular or medium |

Bold is for the number in a stat tile. Everything else is at most semibold.

## Colour

- The accent is for selection, focus and the one main action of an area.
- Borders, dividers and surfaces are neutral.
- Success, warning, danger and info carry meaning only, always as a badge with words.
- Icons sit in a tinted tile only where the tile tells what kind of thing it is (a service category, a notification category, a file type).

## Values

| Never show | Show |
|---|---|
| `en-US` | English (United States) |
| `2015-11-01` | 1 November 2015 |
| `Africa/Cairo` | Cairo (GMT+02:00) |
| `2026-11-09T13:03:00Z` in a list | 2 h, Yesterday, 9 Nov |
| `Source: Directory Who sees this: Everyone` | "Managed by the directory" once per group, and a small eye label only where visibility is narrower |
| `Operations Dubai HQ, Floor 7` | Operations · Dubai HQ, Floor 7 |
| `hr.leave` or `analytics_sentiment` | Leave requests and balances, Sentiment insights (the catalogue name, never the key) |

Use `lib/format.ts` (`languageName`, `dateLong`, `relativeTime`, `timeZoneLabel`) and join separate facts with `joinMeta`.

## Controls

- Hints sit under the control so a row of controls lines up. In the filter toolbar the hint becomes the placeholder.
- Fields in a row have no bottom margin, so buttons line up with the controls.
- Radios and checkboxes use the system look. A yes or no answer is two toggle buttons. A sort direction is an icon button with `aria-pressed`.
- Required fields carry a small "Required" tag after the label.
- Buttons keep their natural width and sit at the start of the area. Full width only on phones and the sign-in card.
- A link in a sentence that starts something (connect, edit, view) is a button.

## Links

- Titles, rows, breadcrumbs, menus and chips are links by position: no underline.
- Links inside running text keep their underline.
- Breadcrumbs are muted, separated by a chevron that mirrors in right to left, and the current page is in primary text.

## Overlays

- One shell panel open at a time; Escape closes and returns focus; an outside press closes; the trigger toggles.
- Panels stay 8px inside the window; under 40rem they open as a bottom sheet.
- Header and footer stay fixed; only the body scrolls, with a thin scrollbar.
- The assistant launcher hides while a shell panel is open, and never covers content at the end of a page.

## States

- Every component has hover, focus, disabled, loading, empty and error.
- Hover changes a border or surface, never size or position.
- Loading is a skeleton in the shape of the content.
- Empty, error and denied show an icon tile, a title and one line of help, with an action when there is one. No dashed boxes.
- A card never ends half empty: its empty state fills it.

## Patterns

| Pattern | Rule |
|---|---|
| Notification row | Category icon tile, title clamped to two lines, meta line, relative time, unread dot |
| Work item | Card form up to six items; row form with aligned columns for more |
| List row | Title link, status badge, one meta line with middle dots, chevron when it opens a page |
| Person card | Avatar aligned to the top of the text; title on its own line; unit · location |
| Profile | Identity header, then Work information, Contact details, Personal preferences |
| Article | Reading column, meta and Edit in the header, On this page and Related at the side, helpfulness at the end |
| Library | Breadcrumbs, compact filters, sort and direction, file rows with icon, type, owner, change date |
| Catalogue | Search by name, filters with counts, groups by area with "x of y", sorted by localized name, two columns on wide screens, empty state with a way back |

## Quality gate

Before merge, check in Beats, Atlas, Pulse and Studio, in Arabic and English, light and dark, at phone, tablet and desktop:

- [ ] No floating panel overlaps another
- [ ] Nothing is clipped
- [ ] Shared parts use tokens only
- [ ] Every component has its states
- [ ] Compact and comfortable densities size things consistently
- [ ] Right to left, dark mode and 200% text work
- [ ] The assistant launcher covers nothing
- [ ] Workflows and permissions are unchanged
- [ ] Key journeys pass the design suite's visual and keyboard tests
