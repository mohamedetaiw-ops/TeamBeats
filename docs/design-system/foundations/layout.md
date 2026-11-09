# Layout

Layout gives every page the same predictable frame so people always know where the navigation, the title and the main action are.

## The frame

- **Shell** (spec 008): header bar with identity, search, notifications, tasks, language, settings and the account menu; then the global navigation; then breadcrumbs.
- **Navigation style** follows the design: Beats uses an icon rail on the start edge, Atlas a top bar of labelled links, Pulse a dark labelled sidebar. On screens up to 1024 px wide all three become the same menu behind one button.
- **Main** holds one `<main id="main">` with one h1. A skip link jumps to it.

## Breakpoints

| Name | Width | Columns | Gutter | Behaviour |
|---|---|---|---|---|
| Phone | up to 640 px | 4 | 16 px | One column, navigation behind the menu button, bottom-aligned primary actions in forms |
| Tablet | 641 to 1024 px | 8 | 24 px | Two columns where content allows |
| Desktop | 1025 to 1440 px | 12 | 24 px | Rail, top bar or sidebar visible; up to three columns of cards |
| Wide | over 1440 px | 12 | 32 px | Content stays at a maximum width of about 1440 px |

Layouts are fluid between breakpoints and must work from 320 px wide (WCAG 1.4.10 reflow) without horizontal scrolling. The design suite checks 375, 768 and 1440 px in every design and direction.

## Spacing

Spacing comes only from `space.xs` 4, `sm` 8, `md` 16, `lg` 24, `xl` 32 and `xxl` 48 px (compact density reduces them). Use `md` inside components, `lg` between cards, `xl` and `xxl` between page sections. Related things sit closer than unrelated things.

## Right to left

- Use logical properties only: `margin-inline-start`, `padding-inline`, `inset-inline-end`, `text-align: start`. stylelint refuses `left` and `right`.
- The whole layout mirrors in Arabic: the navigation sits on the right, back arrows point right, progress runs right to left. Charts with a time axis keep time running in the reading direction.
- Do not mirror media controls, phone numbers, code, or images of real things.

## Templates and regions

Pages are built from the 12 layouts and templates of spec 032 and the [templates](../templates.md) here. Regions have a fixed order in the reading direction; components never set their own width outside their region.
