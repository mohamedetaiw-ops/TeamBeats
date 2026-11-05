# Feature Specification: Content, Publishing and Governance

| Field | Value |
|---|---|
| Spec ID | 009-content-and-publishing |
| Area | experience |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (news, announcements, pages, targeting, approval, versions, review dates, critical announcements with acknowledgement), R2 (sites, content components for the page builder of spec 032, campaigns across channels, AI translation, content health dashboard), R3 (external sites for guests, advanced campaign automation) |
| Last updated | 2026-11-05 |
| Requirement IDs | EXP-002, EXP-003, EXP-004, LOC-004, UC-04, PUB-001 to PUB-020 (new) |
| Use cases | UC-04 |
| Depends on | 002 (org units), 004 (authorization), 005 (entitlements), 006 (audit), 007 (localization), 008 (shell, personalization), 032 (page composition), 033 (themes and organization inheritance), 010 (profiles), 011 (documents and assets), 012 (search), 013 (AI Gateway), 015 (notifications), 021 (analytics), 024 (process engine, for approval routes), 025 (policies), 026 (external users) |

## 1. Problem and outcome

**Problem:** Internal communications teams in large and regulated organizations publish news, announcements and reference pages for many audiences: head office and frontline, several subsidiaries, brands and countries, in more than one language. Today they publish through email blasts and static pages. Nobody can prove who read a critical notice, pages go stale with no owner, approvals happen in chat, and each subsidiary wants its own branded space. Buyers in regulated sectors ask for content approval, named owners, review dates and version history in every tender.

**Outcome:** Communicators write or generate content, send it through an approval route, target it to the right people by org unit, location, role, group or device, and schedule it. Critical announcements interrupt the user and can require an "I have read and understood" confirmation with tracked completion. Every item has an owner, a review date and a version history that can be compared and restored. Sites give each subsidiary, location or brand its own home, theme and editors while staying inside the tenant's security. Communicators see reach and engagement per item and per campaign.

## 2. Scope

**In scope:**
- Content types: news article, blog post, announcement, event, page, knowledge page, corporate offer, vacancy, promotion list. Tenants can add custom content types with extra fields.
- Authoring: rich text editor, media from the asset library (spec 011), embeds from allowlisted hosts, templates, drafts, autosave, co-editing lock, preview per audience and per device.
- AI-assisted authoring: draft from a prompt or source document, rewrite for tone and length, headline and summary suggestions, image generation where the tenant allows it, accessibility checks (alt text suggestions, reading level), translation into the tenant's enabled locales.
- Publishing: publish now, schedule, expire, archive, unpublish, pin to top, feature on home.
- Audience targeting: org units (with or without subtree), locations, groups, roles, job attributes from the profile (spec 010), membership type (employee, contractor, guest), and device channel (web, mobile, kiosk or shared screen).
- Critical announcements: banner, pop-up on next visit, required acknowledgement with a deadline, reminders, escalation to managers, completion report.
- Governance: content owner and backup owner, approval routes run on the process engine (spec 024), review date and review reminders, expiry, version history with compare and restore, content health dashboard, retention per content type.
- Sites (microsites): a site per org unit, location, brand or project with its own navigation, theme, editors and approvers. Site templates.
- Content components for pages: news feed, links, people, documents, events, services, embedded dashboard, video, rich text, call to action. They are registered through component manifests (spec 032) and placed with the page builder of spec 032, on site pages and on the employee home (spec 008).
- Site branding: a site picks a brand profile or overrides approved brand properties under the tenant's locks (spec 033).
- Campaigns: group several items and channels (intranet, email, Teams, mobile push, digital signage) into one campaign with a schedule and shared metrics.
- Social on content: comments, reactions, @mentions, #hashtags, follow a topic or a site, share by link. Moderation settings per site.
- Personal feed: users follow sites and topics and set language and topic preferences, which shape their feed and recommendations. Mandatory and critical content is never hidden by preferences.
- Event content: events calendar per site and tenant, registration with capacity, add to calendar file, reminders.
- Share to external social networks (employee advocacy) for items marked as public by the communicator.

**Out of scope:**
- Document check-in and check-out, retention labels and the asset portal (spec 011).
- Policies with attestation and policy review cycles (spec 025). Policy pages render through this spec's page engine.
- Communities, discussion rooms, polls, surveys and recognition (spec 023).
- Home page personalization (spec 008). The page model, templates, layouts, component registry and visual page builder (spec 032). Themes, tokens and brand inheritance (spec 033). This spec supplies content components to them.
- Notification delivery mechanics (spec 015). This spec decides what to send; 015 delivers it.
- Public internet websites. Sites in this spec are for signed-in users, including guests from spec 026.

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Read targeted and permitted content, react, comment, acknowledge, follow, set feed preferences | CanView on the item; tenant membership |
| Author | Create and edit drafts in sites where they are an author | `content.author` at site scope |
| Editor | Edit and submit any draft in the site, schedule, request translation | `content.edit` at site scope |
| Approver | Approve or return content in their route step | Assigned by the route (spec 024) |
| Publisher | Publish, unpublish, pin, send critical announcements | `content.publish` at site scope; `content.publish_critical` for critical |
| Content owner | Answer for accuracy, receive review reminders, approve changes to owned items | Named on the item |
| Site owner | Manage site settings, navigation, theme, editors, moderation | `site.manage` at site scope |
| Communications admin | Manage content types, templates, tenant theme, campaign channels, retention defaults | `content.admin` at tenant scope |
| Guest (spec 026) | Read content on sites shared with their guest space; no comments unless allowed | Guest membership and site share |

Targeting is not access. A user outside the audience can still open a page they are permitted to read by link, and a user in the audience who is not permitted gets nothing (constitution II).

## 4. User scenarios

### User Story 1: Publish targeted news with approval (Priority: P1, R1)

Layla in Group Communications writes a news article about the new Riyadh office, targets it to the "Saudi Arabia" location and the "Acme KSA" subtree, schedules it for Sunday 08:00 local time and submits it. The site's route sends it to the country manager for approval. After approval it publishes at 08:00 and appears in the feeds of the targeted people only.

**Why this priority:** news is the first thing pilot customers check, and approval is a tender requirement in regulated sectors.

**Independent test:** create an article with an audience and a one-step route; approve it; confirm it appears only for in-audience users at the scheduled time and that the full history is audited.

**Acceptance scenarios:**
1. **Given** a draft with an audience and a site route, **When** the author submits, **Then** the item moves to In review and a task appears in the approver's inbox (spec 016).
2. **Given** the approver returns the item with a comment, **Then** it goes back to Draft, the author is notified, and the comment is kept in the item history.
3. **Given** approval completes before the scheduled time, **Then** the item publishes at the scheduled time in the site's time zone; **Given** approval completes after it, **Then** it publishes immediately and the publisher is told the schedule was missed.
4. **Given** a user in the targeted subtree, **When** the item publishes, **Then** it appears in their feed; **Given** a user in a sibling unit, **Then** it does not appear in their feed and does not appear in search unless they are permitted to read it.
5. **Given** an author edits a published item, **When** they save, **Then** a new draft version is created and the published version stays live until the new version is approved.
6. **Given** an author who is also the only approver in the route, **Then** the route skips to the next eligible approver or the site owner. Nobody approves their own change.

### User Story 2: Critical announcement with required acknowledgement (Priority: P1, R1)

The Health and Safety lead sends a critical announcement about a new evacuation procedure to all staff in two plants, as a pop-up that requires "I have read and understood" within five working days. Managers see who in their team has not confirmed. The lead downloads a completion report for the auditor.

**Why this priority:** proof of reading is a recurring compliance need and the most-asked feature in regulated sectors.

**Independent test:** send a critical announcement with acknowledgement to a test audience; confirm it blocks once per user until confirmed, track completion, run reminders and export the report.

**Acceptance scenarios:**
1. **Given** a critical announcement with the pop-up option, **When** a targeted user next opens any page on web or mobile, **Then** the announcement shows in a modal that they can close only by confirming or by choosing "remind me later" (if the publisher allowed it, up to a set number of times).
2. **Given** acknowledgement is required, **When** the user confirms, **Then** the product records user, version, time, channel and the exact text shown, and does not show the modal again for that version.
3. **Given** the announcement text changes in a way the publisher marks as material, **Then** previous confirmations no longer count and the modal shows again. Minor corrections keep confirmations.
4. **Given** a deadline, **When** it passes, **Then** users who have not confirmed get a reminder, and their line managers get a list of their reports who have not confirmed (spec 010 reporting line).
5. **Given** a publisher with `content.publish` but not `content.publish_critical`, **When** they try to send a critical announcement, **Then** the action is denied and audited.
6. **Given** an announcement banner, **Then** it shows on every page above the content, in the user's locale, until it expires or the user dismisses it (if dismissible).
7. **Given** a completion report request, **Then** the export lists each targeted person, status, time and channel, is generated in the background and is audited.

### User Story 3: Owners, review dates and stale content (Priority: P1, R1)

Every page has a content owner and a review date. Thirty days before the review date the owner gets a task to confirm, update or retire the page. The communications admin sees which content is overdue for review, has no active owner or has not been read in a year.

**Independent test:** set a review date in the near future; confirm the reminder task, the overdue state and the dashboard counts.

**Acceptance scenarios:**
1. **Given** a content type that requires an owner, **When** an author tries to publish without one, **Then** publishing is blocked with a message.
2. **Given** a review date, **When** the reminder window starts, **Then** the owner gets a review task with three actions: confirm as current (sets the next review date), edit (creates a draft), retire (archives after approval).
3. **Given** the review date passes with no action, **Then** the item shows an "under review" label to readers if the content type is configured for it, and the owner's manager and the site owner are notified.
4. **Given** an owner whose tenant membership is disabled (spec 003), **Then** their items move to the backup owner, or to the site owner if there is none, and the change is audited.
5. **Given** the content health dashboard, **Then** it shows counts per site of items overdue for review, without an active owner, expired but still linked, and not viewed in the configured period, with links to each list.

### User Story 4: Version history, compare and restore (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** an item with several published versions, **When** an editor opens history, **Then** they see each version with author, approver, time and change note, and can compare any two versions side by side with additions and removals highlighted.
2. **Given** an editor restores an earlier version, **Then** a new draft is created from it and follows the normal route. History is never rewritten.
3. **Given** a reader with view access only, **Then** they see the current version and its "last updated" date but not drafts or history, unless the content type shows history to readers.

### User Story 5: Content in several languages (Priority: P1, R1 manual, R2 AI translation)

**Acceptance scenarios:**
1. **Given** a tenant with Arabic and English enabled, **When** an author creates an item, **Then** they can add a variant per enabled locale, each with its own title, body and status, linked to one item and one audience.
2. **Given** a user whose locale has no variant, **Then** they see the configured fallback locale with a "not available in your language" note (spec 007).
3. **Given** AI translation is enabled, **When** an editor requests translation, **Then** a draft variant is produced through the AI Gateway (spec 013), marked "machine translated" until a human reviewer approves it, and the route can require a reviewer per locale.
4. **Given** content classified Confidential, **When** AI translation is requested, **Then** it is blocked unless the tenant's AI policy allows that classification for translation (constitution IX).
5. **Given** a variant that falls behind the source after an edit, **Then** it is flagged "out of date" to editors.

### User Story 6: Sites for subsidiaries, locations and brands (Priority: P2, R2)

Acme Gulf wants its own intranet site with its own colors, navigation and editors, and only its own staff should be able to edit it. Group content still reaches Acme Gulf staff.

**Acceptance scenarios:**
1. **Given** a tenant admin, **When** they create a site from a template and link it to an org unit, location or group, **Then** the site has its own home page (a page of spec 032), navigation (spec 008), theme (spec 033), editors, approvers and route.
2. **Given** a site linked to "Acme Gulf", **Then** its default audience is that subtree, and Acme Gulf staff see the site in their navigation. Staff of a sibling subsidiary do not, unless the site is shared with them.
3. **Given** a site theme, **When** the site owner picks colors, **Then** the product applies the contrast validation of spec 033 and keeps tenant-locked elements (for example the tenant logo in the header) as set by the tenant admin.
4. **Given** a site owner of Acme Gulf, **Then** they cannot edit any other site, tenant theme or content type.
5. **Given** a site is archived, **Then** its content is read-only, removed from feeds and navigation, and still found by search for permitted users with an "archived" label.

### User Story 7: Content components on composed pages (Priority: P2, R2)

Page composition itself (templates, layouts, builder, previews, approval of page structure) is specified in spec 032. This story covers what content components must do on those pages.

**Acceptance scenarios:**
1. **Given** a site page built in the spec 032 builder, **When** an editor places content components, **Then** each component works in every compatible region of any template, at every reference width and in both directions, without content-specific builder code.
2. **Given** a widget that shows data (news, documents, people, services, events), **Then** it shows only items the viewing user is permitted to see, each time the page renders.
3. **Given** an embed widget, **Then** only hosts on the tenant's embed allowlist can be embedded, inside a sandboxed frame.
4. **Given** a content page (page content type), **Then** its body text follows this spec's lifecycle (draft, route, versions) and its structure follows spec 032; one publish action covers both.
5. **Given** a content component whose capability the tenant's package does not include, **Then** it is not offered in the builder and the API refuses it.

### User Story 8: Campaign across channels (Priority: P2, R2)

HR runs a four-week benefits enrolment campaign: an article, two announcements, an email, a Teams post and mobile push reminders, with one dashboard of reach and actions.

**Acceptance scenarios:**
1. **Given** a campaign with items and channel sends on a schedule, **When** each step is due, **Then** it is sent through spec 015 to the campaign audience, minus people who already completed the call to action if the step is set to "only those who have not acted".
2. **Given** a campaign dashboard, **Then** it shows per step and in total: audience size, delivered, opened or viewed, clicked, acknowledged or completed, by org unit and location, with the privacy thresholds of spec 021.
3. **Given** a user who has turned off optional email for a category, **Then** optional campaign emails are not sent to them; mandatory communications ignore the preference and are labelled mandatory.

### User Story 9: React, comment and mention (Priority: P2, R1 reactions and comments, R2 mentions and hashtags)

**Acceptance scenarios:**
1. **Given** comments are enabled on the item, **When** a user comments, **Then** the comment shows with their name and photo, and the owner is notified.
2. **Given** a comment with an @mention, **Then** the mentioned person is notified only if they are permitted to view the item.
3. **Given** a #hashtag, **Then** it links to a topic page listing permitted items with that tag.
4. **Given** moderation is on for the site, **Then** comments from users without `content.edit` are held until a moderator approves them; reported comments are hidden after the configured number of reports pending review.

### Edge cases and exception flows
- The targeted audience resolves to zero people: publishing warns and asks for confirmation.
- The audience changes after publishing (people join or leave a unit): feeds and acknowledgement lists follow the current membership; confirmations already given are kept.
- A critical announcement targeted at 50,000 people: modal state and completion counting must not slow page loads (section 15).
- An approver is on leave with a delegate (spec 016): the delegate gets the task.
- A scheduled item whose approval is withdrawn: the schedule is cancelled.
- An item links to a document the reader cannot open: the link shows "you do not have access" instead of the document title.
- AI drafting is unavailable: authoring works as normal and the AI actions show a degraded state (constitution XII).
- Uploaded or pasted HTML: sanitized, scripts removed, only allowlisted embeds kept.
- A site owner leaves: the site is flagged ownerless in the content health dashboard and the communications admin is notified.

## 5. Functional requirements

- **FR-001 / PUB-001**: The system MUST support the content types in section 2 and tenant-defined content types with typed extra fields. *(EXP-002)*
- **FR-002 / PUB-002**: Every content item MUST have a lifecycle of Draft, In review, Approved, Scheduled, Published, Expired, Archived, with every transition audited. *(EXP-004)*
- **FR-003 / PUB-003**: Approval routes MUST be configurable per site and per content type, from none to multi-step, and MUST run on the process engine (spec 024) with tasks in the task inbox (spec 016). An author MUST NOT approve their own item. *(EXP-004)*
- **FR-004 / PUB-004**: Publishing MUST support immediate, scheduled (in the site time zone) and expiry dates, and pinning and featuring per site. *(EXP-002)*
- **FR-005 / PUB-005**: Audience targeting MUST support org units with or without subtree, locations, groups, roles, profile attributes, membership type and channel, combined with AND and OR, and MUST be evaluated separately from access control. *(EXP-003)*
- **FR-006 / PUB-006**: Critical announcements MUST support banner and pop-up presentation, optional required acknowledgement with a deadline, reminders, manager escalation and a completion report. Only users with `content.publish_critical` can send them.
- **FR-007 / PUB-007**: Each acknowledgement MUST record user, item version, time, channel and a hash of the text shown, and MUST be immutable.
- **FR-008 / PUB-008**: Content types MUST be able to require a content owner and a review date. Review reminders, overdue states and ownerless reassignment MUST follow US3. *(EXP-004)*
- **FR-009 / PUB-009**: Every save of a published item MUST create a new version. The system MUST support viewing, comparing and restoring versions without deleting history. *(EXP-004)*
- **FR-010 / PUB-010**: Items MUST support locale variants with fallback, and AI translation drafts that require human approval before publishing. *(LOC-004)*
- **FR-011 / PUB-011**: AI-assisted authoring MUST go through the AI Gateway, respect the tenant AI policy for the item's classification, label AI-generated text and images until a human edits or approves them, and never publish without a human action. *(constitution VIII, X)*
- **FR-012 / PUB-012**: The system MUST support sites with their own home page, navigation, theme, editors, approvers, route, moderation and default audience, created from site templates. Pages, navigation and theme use specs 032, 008 and 033.
- **FR-013 / PUB-013**: Content components MUST be registered through spec 032 manifests, render data with the viewer's permissions on every render, and allow embeds only from the tenant allowlist in a sandboxed frame.
- **FR-014 / PUB-014**: Sites MUST use the brand profiles, overrides, locks and contrast validation of spec 033 at site scope. *(ADM-001, EXP-008)*
- **FR-015 / PUB-015**: Campaigns MUST group items and channel sends on a schedule with "only those who have not acted" targeting and one dashboard. Delivery MUST go through spec 015.
- **FR-016 / PUB-016**: Comments, reactions, mentions and hashtags MUST be configurable per site and content type, with moderation, reporting and retention.
- **FR-017 / PUB-017**: Users MUST be able to follow sites and topics and set feed preferences. Recommendations MUST only use the user's own signals and permitted items, and MUST NOT hide mandatory or critical content.
- **FR-018 / PUB-018**: Event items MUST support date and time with time zone, location or online link, capacity, registration, waitlist and calendar file download.
- **FR-019 / PUB-019**: All published items MUST be indexed for search (spec 012) with their audience, permissions, locale, version and classification, and removed from the index within the KNO-006 objective (5 minutes by default, owned by spec 012) when unpublished or restricted.
- **FR-020 / PUB-020**: Retention MUST be configurable per content type: keep versions for a period, then delete old versions or the whole item, with legal hold (spec 011) taking precedence. *(ADM-003)*

## 6. Business and validation rules
- An item has one primary site. It can be shared into other sites as a link, never as a copy, so there is one version and one owner.
- Critical announcements are limited to the publisher's own site audience unless they hold `content.publish_critical` at tenant scope.
- "Remind me later" on a pop-up is allowed at most 3 times per user per version by default.
- Default review period: 12 months for pages and knowledge pages; none for news. Tenant configurable per content type.
- Expired items stay readable by link with an "expired" label for 30 days, then archive. Configurable.
- Material change on an acknowledged item resets acknowledgement. The publisher must choose material or minor at publish time.
- Machine-translated variants cannot be published without a reviewer approval when the content type requires reviewed translation.

## 7. Tenant and organization scope
- Tenant-owned data: content items, versions, variants, sites, themes, templates, acknowledgements, comments, reactions, campaigns, follows.
- Organization scoping: site scope (linked to an org unit subtree, location or group) and item audience. Authoring roles are granted at site scope.
- Cross-tenant denial cases: reading an item, its versions, comments or acknowledgement report by ID from another tenant; embedding a widget that queries another tenant; a campaign audience that includes another tenant's group ID.
- Sibling, parent and child boundary cases: a site owner of a sibling subsidiary cannot edit; a parent-level editor does not gain edit rights on child sites unless granted with subtree scope; targeting a parent without subtree does not reach children.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Content item and versions | Product | Per item, default Internal | Content type setting | Author and owner IDs |
| Locale variant | Product | Same as item | Same as item | No |
| Acknowledgement record | Product | Internal | At least as long as the item plus 7 years by default | Yes |
| Comment, reaction | Product | Internal | Site setting, default life of item | Yes |
| Site, theme, template | Product | Internal | Life of tenant | No |
| Campaign and send log | Product | Internal | 2 years default | Yes (recipients) |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `content.core` (news, pages, targeting, versions, one-step approval) | on | on | on |
| `content.critical_ack` (critical announcements with acknowledgement) | on | on | on |
| `content.multistep_approval` | off | on | on |
| `content.max_sites` (limit) | 3 | 25 | unlimited |
| `content.campaigns` | off | on | on |
| `content.ai_translation` | off | on | on |
| `ai.authoring` (AI drafting and rewrite, counts against AI quota) | on | on | on |

## 10. UX, localization and accessibility
- States: empty site, draft, in review, scheduled, published, expired, archived, under review, machine translated, out of date variant, AI unavailable, no permission.
- Editor works fully in right-to-left for Arabic and Urdu, including mixed-direction text, tables and embeds.
- Pop-up announcements are accessible modals: focus is trapped and returned, the confirm control is labelled, and screen readers announce the title. They never auto-close.
- Compare view does not rely on color alone; additions and removals also carry text markers.

## 11. AI behavior and data policy
- Drafting, rewriting, summaries, translation and image generation go through the AI Gateway (spec 013) with the item's classification. Restricted content never goes to external providers by default; Confidential needs tenant approval (constitution IX).
- Source documents used for drafting are read with the author's permissions only.
- Generated text and images are labelled until a human edits or approves them. Generated images carry a hidden "AI generated" marker in their metadata.
- Content written by employees is untrusted input to AI features (constitution XI): instructions inside drafts are never executed as tool calls.
- AI sentiment on comments is in spec 021 and works on aggregates only.

## 12. Integrations and events
- Publishes: `ContentPublished`, `ContentUnpublished`, `ContentExpired`, `ContentAcknowledged`, `ContentReviewDue`, `CampaignStepSent`.
- Consumes: `UserDeprovisioned` (owner reassignment), `OrganizationMembershipChanged` (audience and acknowledgement lists), approval results from spec 024.
- Channels through spec 015: email, Microsoft Teams (spec 018), mobile push, digital signage feed (read-only signed URL per screen).
- Share to external social networks only for items marked public, through share links, with no tokens stored for user social accounts.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| content.submitted / approved / returned / published / unpublished / archived | audit | item, version, site, actor, route step |
| content.critical_sent | audit | item, audience size, acknowledgement settings |
| content.acknowledged | audit | item, version, user, channel |
| content.version_restored | audit | item, from version, actor |
| content.owner_reassigned | audit | item, from, to, reason |
| site.created / editors_changed | audit | site, actor (theme and page changes are audited by specs 033 and 032) |
| content.viewed, content.reacted, content.commented | analytics | item, user (pseudonymous in analytics), channel, dwell bucket |
| campaign.step_delivered / opened / clicked | analytics | campaign, step, channel |

## 14. Security and abuse cases
- A compromised editor account publishes a fake critical announcement: critical sends need `content.publish_critical`, are rate-limited, can require a second approver per tenant setting, and can be recalled with one action, which removes the modal and notifies recipients.
- Stored cross-site scripting through rich text or embeds: server-side sanitization with an allowlist, sandboxed frames, content security policy.
- Using @mentions to find out who exists in a restricted unit: mention suggestions only list people the author can see in the directory (spec 010).
- Inferring restricted content through feed counts or hashtags: counts only include permitted items.
- Acknowledgement report export is a personal data export: needs `content.publish_critical` or `content.admin` and is audited.

## 15. Non-functional requirements
- Feed and page render p95 under 800 ms server-side for a user with 50 audience memberships and 10,000 published items in the tenant.
- The critical announcement check adds no more than 20 ms p95 to page loads.
- A critical announcement to 100,000 people creates its acknowledgement tracking within 2 minutes.
- Scheduled publishing fires within 60 seconds of the scheduled time.

## 16. Success criteria
- **SC-001**: A communicator can write, target, route and schedule a news article in under 10 minutes on first use.
- **SC-002**: A compliance officer can produce a who-has-confirmed report for a critical announcement in under 2 minutes.
- **SC-003**: In a pilot tenant, at least 90% of pages have an active owner and no more than 5% are overdue for review after six months.
- **SC-004**: A subsidiary site owner can launch a branded site from a template in under one hour without help.

## 17. Open questions
- [NEEDS CLARIFICATION: should critical announcements need a second approver by default?] Default: no, tenant setting.
- [NEEDS CLARIFICATION: is digital signage needed in R2 or later?] Default: R2 read-only feed.
- [NEEDS CLARIFICATION: should employees be able to write blog posts without approval?] Default: yes on personal and community blogs with moderation; no on official sites.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| EXP-002 | FR-001, FR-004, US1 |
| EXP-003 | FR-005, US1 |
| EXP-004 | FR-002, FR-003, FR-008, FR-009, US3, US4 |
| LOC-004 | FR-010, US5 |
| ADM-001 (branding) | FR-014, US6 |
| ADM-003 | FR-020 |
| UC-04 | US1, US2 |

## Review checklist
- [x] No implementation details
- [x] Every FR is testable and traces to a requirement ID or is marked new
- [x] Cross-tenant and org boundary cases are listed
- [x] Entitlement behavior is defined
- [x] Audit events are defined
- [ ] No open [NEEDS CLARIFICATION] items that change scope
