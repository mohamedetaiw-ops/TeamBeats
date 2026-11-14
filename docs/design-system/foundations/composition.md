# Composition

A page is composed, not stacked. The parts of the system (cards, rows, tables, charts) are only as good as the page they make together. This page sets how every screen, for employees and for administrators, is put together from them. It is binding through constitution XXXI and spec 036 DLS-100 to DLS-119.

## Three levels on every page

Before choosing components, name three things for the screen and give them visual weight in this order (DLS-100):

1. **Primary action or decision.** What the person must do here, or the answer they came for. It sits in the first viewport, takes the widest region and holds the page's one primary button.
2. **Supporting context.** What helps them do it: what is next, who is involved, the status.
3. **Discovery.** What they may also want: news, suggestions, related items. It comes last and never pushes the first two down.

| Page | Primary | Supporting | Discovery |
|---|---|---|---|
| Home, My day | What needs your attention, each with its action | Next up: the next meeting, free time, time off | Your services, the lead story, calendar and mail summaries |
| Home, Team lead | Approvals by age and requests to escalate | Team availability and leave, upcoming one to ones | Team milestones, news |
| Home, Plant floor | Site and safety announcements, key actions | Shift schedule, transport | Quick services, news |
| Tasks | The action inbox | The item in review, beside the queue | Delegation summary |
| Services | Search and your services | Categories | Recommended services |
| Calendar | The selected range in the chosen view | The day panel | Layers |
| Assistant | The conversation and its composer | Sources of the answer | Past conversations, suggestions |
| People | Search and results | Filters | New joiners |
| Policies | What you must acknowledge, or "You are up to date" | What changed | All your policies |
| A register or admin list | What needs attention | The toolbar and the table | Help |

A screen made of equal white cards has no hierarchy. If two things look equally important, decide which one is.

## Choosing a pattern

Use the pattern that fits the information (DLS-107). A card is not the default.

| The person needs | Use | Do not use |
|---|---|---|
| One important count and what to do about it | A labelled number with its action | A ring or gauge, a card with only a number |
| A small trend | A number with a sparkline and a comparison ("up 4 from last week") | A number alone, a chart alone |
| Status across stages | A progress line with owner, age and next step | The status word alone |
| Several comparable categories | A ranked bar list | A pie with many colours |
| Today's appointments | A short agenda in time order | A list of several days on a summary |
| Availability over days | A week or month view | A text agenda with no overview |
| Two to six frequent destinations | Icon and title shortcuts in one row | Tall cards with descriptions |
| Many searchable records | A dense table or list with the filter toolbar | Hundreds of cards |
| Work waiting for a decision | Work rows with the next action in the row | Rows that only link somewhere |
| A story, a campaign, a person in the spotlight | An image-led editorial unit, or the typographic card when there is no picture | A coloured placeholder block |
| Discrete things to browse and pick (services, people, spaces, libraries) | Cards in a grid, with a list view | Cards for records, settings or text |
| Input that needs explaining | A guided form with steps | One long form with a "Go" button |
| A rare, consequential setting beside a list | A summary with one button that opens a guided dialog | A permanent form beside the list |

Two tests catch most mistakes. If every card in a grid has the same icon and one line of meta, it is a list. If every row in a column has the same value, hide the column and say the value once (DLS-109).

## Content budgets

Summaries stay short so pages stay short (DLS-101). The `Widget` frame enforces the budget; a feature cannot pass more rows.

| Region width | Items shown | Then |
|---|---|---|
| Quarter | 3 | "View all" with the total |
| Half | 5 | "View all" with the total |
| Full | 8 | "View all" with the total |
| A strip of tiles | One row, 4 to 6 | "All services" style link |

- A summary never scrolls inside itself.
- An empty summary shows its empty state at its natural height. No blank area taller than 96 px.
- With 30 tasks and 20 meetings the home is at most 2.5 viewports tall on desktop.

## One instance per source

A page shows a data source once (DLS-102): one mail list, one calendar, each count once. Two placements of one source are allowed only as different views (a month grid and a day agenda). Where the greeting says "15 things need you", the side panel shows what is next, not the same counts again. The renderer drops a duplicate and the page builder warns.

## Every state is designed

A component is finished when all its states are (DLS-103): first load as a skeleton in the content's shape, empty with a reason and a next step, partial data, stale with a refresh, error with Retry inside the component, denied without leaking names, success as a positive message ("You are up to date"), disabled, pending, long text in Arabic and English, a narrow container, keyboard focus, dark mode and right to left.

## The filter toolbar

Lists are found through one compact row, not a form in a card (DLS-106): search first with the hint as its placeholder, up to three filters at the same height, view and sort at the end, further filters behind a "Filters" button with a count, applied filters as removable chips under the row with "Clear all". The list starts close under it.

## Form levels

| Level | When | Shape |
|---|---|---|
| Inline | One or two values changed in place | Edit in the row; saved on blur or with a small Save |
| Standard | A request or settings group | Grouped fields in the readable column, one primary action |
| Guided | Several dependent steps or a consequential change | Dialog or wizard with steps, a summary before the commit and a receipt after |

## Five settings that stay separate

| Setting | Owns | Chosen by |
|---|---|---|
| Brand identity | Logo, colours, fonts, imagery | Tenant or organization brand manager |
| Design | The visual personality: tokens, navigation style, default density, presentation variants | Brand manager, from the four designs |
| Layout preset | Which components sit where (home views, page templates) | Page editors; people within the delegated regions |
| Appearance mode | Light, dark or system | The person |
| Density and accessibility | Comfortable or compact, text size, reduced motion | The person, within policy |

Changing one never resets another. Their resolution order is the order of spec 033 FR-004, and the hard constraints (focus, contrast, keyboard access, the 12 px text floor) apply after it.

## One system for administration and employees

Administration pages use the same frame, page header, tabs, toolbar, tables, tags, forms and states as employee pages (DLS-116). There is no administration style. The only differences are the administration navigation and denser tables by default.
