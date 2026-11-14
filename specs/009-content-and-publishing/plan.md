# Implementation Plan: Content, Publishing and Governance

**Spec:** `specs/009-content-and-publishing/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** `docs/architecture/r0-solution-structure.md`, ADR-0010 (workflow engine), ADR-0012 (composable experience), ADR-0013, ADR-0014, module map (`Content`)

## Summary
R1 builds news, announcements and content pages with targeting, approval, scheduling, versions, review dates and critical announcements with acknowledgement (User Stories 1 to 5, manual translation only), content components for the page builder (User Story 7), and reactions and comments (User Story 9, R1 part). Sites with their own home, navigation and theme (User Story 6), campaigns (User Story 8), AI translation, mentions and hashtags are R2.

R1 slices, in order:
1. **Items and lifecycle:** content types, items, versions, locale variants, lifecycle and audit; publish now, schedule and expire.
2. **Approval:** one-step routes per site and content type on the process engine core (spec 024 R1) with tasks in the task inbox (spec 016).
3. **Targeting and feed:** audiences, the personal feed API and the news feed component.
4. **Critical announcements:** banner and pop-up, acknowledgement records, reminders, manager lists and the completion report.
5. **Governance:** owners, review dates, review tasks, ownerless reassignment, version compare and restore, the health counts.
6. **Components and social:** news feed, rich text, links, call to action and announcement banner components; reactions and comments with moderation.
7. **Authoring UI:** editor, audience picker, preview per audience and device, history and compare, in English and Arabic, RTL.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation and org scope | Yes | All tables tenant-owned with forced RLS; site scope is an org subtree; targeting is evaluated apart from access (FR-005), and every feed and component read filters by `IAuthorizationService` |
| III authorization | Yes | `content.view`, `content.edit`, `content.publish`, `content.publish_critical`, `content.admin` at site scope through the central service; nobody approves their own item |
| VII, VIII AI | Yes | No AI in the R1 slice except through the AI Gateway once spec 013 is built (drafting is a later task) |
| XII degraded states | Yes | Feed and components have empty, error and no permission states (spec 008 shared states) |
| XIV modules | Yes | `Content` owns items; `Workflow` runs routes; `Services` shows tasks; `Search` consumes `ContentPublished`; `Notifications` delivers |
| XXI entitlements | Yes | Section 9 keys through `IEntitlementService`; `content.max_sites` as a limit |
| FR-025 of 026, rule 8 | Yes | Body text is a validated structured document, never stored HTML; all labels are resource keys |

## Sites in R1 (spec gap, smallest safe default)
Items have one primary site (section 6), roles are granted at site scope, and `content.max_sites` limits sites, but sites with their own home, navigation and theme are R2 (US6). R1 default: a site is a container with a name in each locale, an org unit scope (subtree), editors, publishers, a default audience, a time zone and an approval route per content type. Each tenant gets one site, "Intranet", scoped to the whole tenant, created on first use. Site home pages, navigation and branding come with US6.

## Targeting dimensions in R1 (spec gap, smallest safe default)
FR-005 lists org units, locations, groups, roles, profile attributes, membership type and channel. R1 builds org units (with or without subtree), directory groups (spec 003), roles (spec 004 assignments) and membership type. Locations and profile attributes wait for the people profile (planned spec 010); channel is web only until the mobile app (planned spec 030). The audience model stores all dimensions so later ones need no migration.

## Data model (schema `content`)
- `site`: `tenant_id`, `id`, `names jsonb`, `org_unit_id`, `time_zone`, `default_audience jsonb`, `routes jsonb` (content type to route: none or one step with approver rule), `comments_mode`, `moderation`, `version`.
- `content_type`: built-in rows (`news`, `announcement`, `page`) plus tenant types later; `requires_owner`, `review_months`, `show_history_to_readers`, `under_review_label`.
- `content_item`: `tenant_id`, `id`, `site_id`, `type`, `classification`, `owner_user_id`, `backup_owner_user_id`, `review_due_at`, `audience jsonb`, `pinned`, `featured`, `critical jsonb` (presentation, acknowledgement, deadline, remind-later limit), `status`, `published_version`, `scheduled_at`, `expires_at`, `version` (row version).
- `content_version`: `item_id`, `number`, `variants jsonb` (per locale: title, summary, body document, status), `change_note`, `material bool`, `author_user_id`, `approver_user_id`, `created_at`. Immutable after publish.
- `acknowledgement`: `tenant_id`, `item_id`, `version_number`, `user_id`, `channel`, `text_hash`, `at`. Insert only (FR-007).
- `audience_member`: materialized membership of critical items for counting (item, user, status), refreshed on `OrganizationMembershipChanged`, so the page-load check is one indexed lookup (section 15).
- `comment`, `reaction`: per item, with moderation state and report count.

## Body document
The body is a JSON document of blocks (paragraph, heading, list, quote, table, image from the asset library, link, embed from the tenant allowlist) with inline marks (bold, italic, link). The API validates it against a schema and rejects anything else, so there is no HTML to sanitize and nothing executable (spec 026 FR-025). The web app renders blocks with React. The editor is a block editor built on Lexical (MIT licence, Meta): the current stack has no rich text editor, and writing one that handles RTL, mixed direction, IME input and accessibility would cost more than it saves. This is the only new dependency and is listed for approval.

## Approval (FR-003)
A route of one step (R1, `content.multistep_approval` adds steps in R2) is a process definition on the spec 024 engine core: submit starts an instance with the item version, the user task goes to the approver rule (named users, role at the site's unit, or line manager once spec 010 exists), and the task shows in the spec 016 inbox. The author is excluded from candidates; if no one else is eligible, the site's publishers get it (US1 scenario 6). Approve, return with comment, and withdraw map to item transitions.

## Scheduling and expiry
A scheduler job on the outbox worker publishes due items each minute (section 15: within 60 seconds) and expires items, with the tenant context set per tenant as in the RLS backfill pattern (PR #59).

## Feed and critical announcements
- `GET /api/v1/content/feed` returns published items whose audience includes the viewer and which the viewer may read, newest first with pinned first, paged by cursor.
- `GET /api/v1/content/critical/pending` returns at most the next critical item for the viewer; the web shell calls it once per navigation and shows the modal or banner (spec 008 shell slot). Confirm and remind later are `POST` actions. Text hash is SHA-256 of the shown variant.
- Reminders and manager lists after the deadline go through spec 015 notifications; the manager list waits for the reporting line of spec 010 and is listed in the notes until then.
- Completion report: a background export (CSV) with audit `content.ack_report_exported`.

## Governance
- Review tasks are user tasks in the inbox, created 30 days before `review_due_at` by the scheduler; confirm, edit and retire actions.
- `UserDeprovisioned` moves owned items to the backup owner or the site's first publisher, audited `content.owner_reassigned`.
- Compare is computed on the server per block with additions and removals marked in text as well as color.
- Health counts per site: overdue for review, no active owner, expired but still linked (from page placements), not viewed (once spec 021 view counts exist).

## API (v1)
- Sites: `GET/POST /api/v1/content/sites`, `GET/PUT /api/v1/content/sites/{id}`.
- Items: `GET/POST /api/v1/content/items`, `GET/PUT /api/v1/content/items/{id}` (draft), `POST .../submit`, `.../publish`, `.../unpublish`, `.../archive`, `.../restore {version}`, `GET .../versions`, `GET .../compare?from=&to=`.
- Reading: `GET /api/v1/content/feed`, `GET /api/v1/content/items/{id}/view`, `GET /api/v1/content/critical/pending`, `POST /api/v1/content/items/{id}/acknowledge`, `.../remind-later`.
- Reports: `POST /api/v1/content/items/{id}/ack-report`, `GET /api/v1/content/health`.
- Social: `GET/POST /api/v1/content/items/{id}/comments`, `POST .../reactions`, moderation actions.

## Components (spec 032 manifests)
`content.news_feed`, `content.rich_text`, `content.links`, `content.call_to_action`, `content.announcement_banner`, each with a manifest, every state of spec 008, reading only through the APIs above with the viewer's session. The documents, people, services and events components belong to specs 011, 010, 016 and the R2 event work.

## Events and search
Publishes `ContentPublished`, `ContentUnpublished`, `ContentExpired`, `ContentAcknowledged`, `ContentReviewDue` with IDs and audience facts only. Search (planned spec 012) consumes them; until it exists the events are published and tested.

## Tests
- Integration per user story: US1 scenarios 1 to 6, US2 scenarios 1 to 7 (manager list once spec 010 exists), US3 scenarios 1 to 5, US4 scenarios 1 to 3, US5 scenarios 1, 2 and 5, US7 scenarios 1 to 5, US9 scenarios 1 and 4.
- Boundaries: item, version, comment and report IDs of another tenant not found; sibling site editor denied; parent editor without subtree scope denied; targeting a parent without subtree does not reach children; a targeted user without read permission sees nothing.
- Entitlements: critical acknowledgement and site limits per package.
- Performance: feed p95 under 800 ms with 10,000 items and 50 audience memberships; pending critical check under 20 ms p95; 100,000-person acknowledgement tracking within 2 minutes.
- Acceptance (e2e): write, target, route and publish news; critical pop-up and confirm; history and compare; in English and Arabic with axe.

## R2 plan (2026-11-14)
R2 builds User Story 6 (sites), User Story 8 (campaigns), AI translation (User Story 5 scenarios 3 and 4), mentions and hashtags (User Story 9 scenarios 2 and 3), follows (FR-017), multi-step approval, retention (FR-020), the content health dashboard and the read-only digital signage feed (open question default).

R2 slices, in order:
1. **Sites (US6, FR-012, FR-014):** a site gets a home page (a spec 032 page owned at the site's org unit), its navigation is the organization menu of spec 008 at that unit, and its theme is the spec 033 brand assignment at that unit, so contrast checks and tenant locks apply unchanged. Sites are created from site templates (product configuration: names, default content type routes, comments and moderation, and the page template of the home). A site has a status of active or archived and an optional `shared_audience`. `GET /api/v1/content/sites/mine` lists the active sites whose default or shared audience includes the reader, for the navigation. Archiving makes items read-only, removes them from feeds and the site from navigation, and marks them archived in search.
2. **Mentions, hashtags and follows (US9 scenarios 2 and 3, FR-017):** comments and item bodies carry mention marks (user IDs) and hashtags (normalized text). A mention notifies the person only when they may read the item; suggestions come from the directory with the author's permissions (spec 010). A topic page lists permitted published items with a tag. People follow sites and topics; the feed gets a "following" filter. Followed content never hides critical or mandatory items, and counts only include permitted items.
3. **AI translation (US5 scenarios 3 to 5, FR-010, FR-011):** `POST /items/{id}/translate {locale}` sends the source variant through the AI Gateway with the item's classification as use case `translate`, writes a draft variant marked `machineTranslated`, and the route can require a reviewer per locale (content type setting `reviewed_translation`). A machine translated variant cannot be published until a person approves or edits it. Confidential content is blocked unless the tenant AI policy allows it. Capability `content.ai_translation`.
4. **Multi-step approval (FR-003):** a site route per content type may have up to five steps, each with its own approvers; the process engine runs the steps in order with an inbox task per step. Capability `content.multistep_approval`; without it a route keeps one step.
5. **Campaigns (US8, FR-015):** a campaign groups items and channel steps (in-app, email, Teams, push through spec 015) on a schedule for one audience, with "only those who have not acted" per step. The scheduler sends due steps; the dashboard shows per step and in total audience, delivered, viewed, clicked and acknowledged, with the spec 021 privacy threshold. Optional sends respect notification preferences; mandatory steps are labelled. Capability `content.campaigns`.
6. **Retention, health and signage (FR-020, section 4 edge cases):** per content type retention (keep versions for a period, then delete old versions or the whole item) run by the scheduler, skipped under legal hold (spec 011 contract). The content health dashboard adds ownerless sites, archived sites with live links and overdue counts per site. A digital signage screen is a named feed with a signed read-only URL per screen showing published, non-confidential items of one site and audience everyone.

## R2 gaps and smallest safe defaults
- Site templates are versioned product configuration (like the service template pack), three to start: subsidiary, location, brand. Tenants edit the resulting site; tenant-defined site templates wait for a later release.
- Site sharing (US6 scenario 2) is an extra audience on the site; it only widens who sees the site in navigation and its items' default audience, never who may edit.
- Mentions in bodies are a mark on text runs carrying the user ID, so no names are stored in the document.
- Hashtags are lower case, letters, digits and underscore, 2 to 50 characters, at most 10 per item; the topic page is `/content/topics/{tag}`.
- Translation reviewer per locale is a content type setting, off by default; when off, publishing a machine translated variant still needs a human to open and approve it (the "machine translated" mark is cleared only by a human edit or approval).
- Retention defaults: keep everything (no deletion) until a tenant sets a period.
- Signage screen URLs carry a random token stored only as a hash; rotating the token revokes the old URL.
