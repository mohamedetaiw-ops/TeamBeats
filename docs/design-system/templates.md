# Templates

Templates are ready page patterns built only from the shared components. Administrators start pages from them in the page builder (spec 032); developers use the same patterns for module pages. The gallery's Templates section lists them in the active design.

| Template | Spec 032 layout | Contents | Use for |
|---|---|---|---|
| Home | Home layout with hero and grid regions | A compact greeting, then the attention queue (wide) beside the next up panel (narrow), then your services beside the lead story, then calendar and mail summaries and role components, the campaign band last. Each home view (My day, Team lead, Plant floor) is its own preset for its kind of work (spec 008 FR-067 to FR-069). Cards for features not built yet stay hidden | Employee home, organization homes |
| List | Contained page | Page header, section tabs, what needs attention, the filter toolbar, data table or list rows, empty state | Policy register, requests, my policies |
| Catalogue | Contained page | Page header, filter toolbar, tile cards grouped by category, favourites | Services, knowledge spaces, document libraries |
| Directory | Contained page | Page header, section tabs, search, person cards | People, my team |
| Detail | Split view | Page header with actions, main content, side column for related actions | A policy, a request, tasks with delegation |
| Article | Article layout | Title, metadata, hero `Media`, readable column (45 to 80 characters), related links, acknowledgement when required | News, announcements, policies, knowledge articles |
| Listing | Listing layout | Title, search and filters, results as `Card` grid or table rows, paging, empty state | News archive, services catalogue, document libraries, people search |
| Form | Single column | Title, short intro, grouped fields, one primary action, inline errors, success state | Service requests, leave, feedback, settings |
| Dashboard | Grid of tiles | Key numbers with labels, trends with table alternatives, the tasks behind them | Analytics, compliance, admin overviews |
| Profile | Two column | `Avatar`, name, role, team, contact, skills, `Tabs` for details | People profiles, team and project spaces |

Spec 032 ships 12 page templates (`T01` to `T12`) that implement these patterns as configurable regions. They inherit the tenant's design automatically: the same template looks like Beats, Atlas or Pulse without any change.

## Page patterns

The extended catalogue adds five patterns built from `components/ui/Patterns.tsx` and the shared blocks. The gallery page `/ui-gallery/patterns` shows each one with real content, and the design suite checks it in every design and direction.

| Pattern | Spec 032 template and layout | Parts | Use for |
|---|---|---|---|
| Dashboard | T08 Dashboard on L08 Mosaic | `PageHeader` with a period control, `StatCards`, `Mosaic` tiles of chart cards, a task list and stage bars | Analytics, workplace and admin overviews |
| Assistant chat | T07 Workspace on L05 Sidebar and canvas | `ChatLayout` with grouped past conversations, `ChatTranscript` as a log, citations and sources, `ThinkingIndicator`, `Composer` | The assistant, agent conversations |
| Agent profile | T09 Detail on L03 Asymmetric two columns | `ProfileLayout` with status, facts and actions, `Tabs` for overview and runs, `UsageLimit` and access facts beside them | Agent pages in the agent registry |
| Record profile | T09 Detail on L03 with L09 tabs | `ProfileLayout`, `FactList` details, `Timeline` activity, people and related items beside them | A request, a case, an asset, a policy record |
| Board | T07 Workspace on L01 with columns along the line | `Board` columns by stage; cards move with their menu or by dragging, each move is announced | Requests by stage, campaign or onboarding work |

## Rules

- Every page states its three levels (primary action, supporting context, discovery) and its states; see [Composition](foundations/composition.md).
- Summary components keep to their content budget and a page shows a data source once.
- Administration pages use the same templates and parts as employee pages.
- One h1 per page, set by the template.
- Regions keep their order in the reading direction; components never break out of their region.
- Every template is checked at 375, 768 and 1440 px, in Arabic and English, light and dark, before release.
- Templates name no concrete component (constitution XXX); they define regions and allowed categories.
