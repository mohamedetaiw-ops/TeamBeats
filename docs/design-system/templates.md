# Templates

Templates are ready page patterns built only from the shared components. Administrators start pages from them in the page builder (spec 032); developers use the same patterns for module pages. The gallery's Templates section lists them in the active design.

| Template | Spec 032 layout | Contents | Use for |
|---|---|---|---|
| Home | Home layout with hero, main and side regions | Greeting or hero, your day (tasks), news, your services, tools, call to action | Employee home, organization homes |
| Article | Article layout | Title, metadata, hero `Media`, readable column (45 to 80 characters), related links, acknowledgement when required | News, announcements, policies, knowledge articles |
| Listing | Listing layout | Title, search and filters, results as `Card` grid or table rows, paging, empty state | News archive, services catalogue, document libraries, people search |
| Form | Single column | Title, short intro, grouped fields, one primary action, inline errors, success state | Service requests, leave, feedback, settings |
| Dashboard | Grid of tiles | Key numbers with labels, trends with table alternatives, the tasks behind them | Analytics, compliance, admin overviews |
| Profile | Two column | `Avatar`, name, role, team, contact, skills, `Tabs` for details | People profiles, team and project spaces |

Spec 032 ships 12 page templates (`T01` to `T12`) that implement these patterns as configurable regions. They inherit the tenant's design automatically: the same template looks like Beats, Atlas or Pulse without any change.

## Rules

- One h1 per page, set by the template.
- Regions keep their order in the reading direction; components never break out of their region.
- Every template is checked at 375, 768 and 1440 px, in Arabic and English, light and dark, before release.
- Templates name no concrete component (constitution XXX); they define regions and allowed categories.
