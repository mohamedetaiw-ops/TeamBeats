# Feature Specification: Experience Shell, Navigation, Employee Home and Personalization

| Field | Value |
|---|---|
| Spec ID | 008-experience-shell-and-personalization |
| Area | experience |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (global shell, shared experience states, messaging rules, multi-level navigation, quick navigation, employee home with add, hide, reorder and reset in delegated regions, preference profile, audience targeting with fallback, interaction standards), R2 (resize, personal layout presets, device-specific choices, preference export, favorites and recents, command entry point, focus mode, in-context help and tours, safe suggestions), R3 (behavioral personalization if the tenant enables it) |
| Last updated | 2026-11-05 |
| Approved | 2026-11-05 by Mohamed |
| Requirement IDs | EXP-001, EXP-008, CX-EXP-001 to CX-EXP-010, CX-NAV-001 to CX-NAV-010, CX-PER-001 to CX-PER-015, CX-INT-001 to CX-INT-012 |
| Use cases | UC-02 (employee home) |
| Depends on | 003 (sessions), 004 (authorization, targeting is not access), 005 (entitlements), 006 (audit), 007 (localization and accessibility), 012 (search), 015 (notifications), 016 (services and task inbox), 021 (analytics), 032 (page composition), 033 (themes and scopes) |

Source: [Composable Intranet Experience requirements](../../docs/product/composable-experience-requirements.md). IDs from that document carry a `CX-` prefix here.

## 1. Problem and outcome

**Problem:** Employees reach the intranet through one frame: the header, menus, search box, notifications and their home page. If each module draws its own buttons, messages and empty states, the product feels like several products. If the home page is fixed, people cannot put their own tools first; if it is fully open, they can remove a mandatory safety notice. Large groups also confuse users when two subsidiaries have pages with the same name.

**Outcome:** One shell frames every page with identity, global navigation, search, notifications, contextual actions, breadcrumbs and settings, configured by admins without touching page components. Every component shares the same states, messages and interaction rules. Employees personalize only the regions and settings admins delegate to them, by pointer or keyboard, on any device, and can always undo or go back to the organization default. Targeted experiences fall back to a safe default, and nothing an employee chooses can widen what they are allowed to see.

## 2. Scope

**In scope:**
- Global shell: identity and profile menu, global navigation, organization or workspace indicator, search and quick navigation, notifications entry, task badge, contextual actions, breadcrumbs, settings entry, optional utilities (clock, language, help). Shell layout is configuration (spec 032 model, spec 033 locks).
- Shared experience states and messaging: loading, success, empty, error, permission denied, offline, unsupported; announcements, confirmations, toasts and dialogs with priority and dismissal rules.
- Interaction standards every component inherits: action hierarchy, validation, feedback timing, destructive actions, input parity, copy rules, density, motion, skeletons, assistance, touch targets, selection and bulk actions.
- Navigation: global, organization, section and contextual menus configured independently; information architecture (labels, hierarchy, slugs); breadcrumbs from the IA; cross-scope indicator; favorites and recents; dead-end recovery; in-context help and guided tours.
- Employee home page: a page from spec 032 with protected regions (mandatory and critical items) and delegated personal regions. Home components owned by other modules include My services strip (016), task inbox summary (016), news and announcements (009), leave calendar (022), My mail and My calendar from the employee's Outlook (018), people and newcomers (010) and tool box utilities (weather, world clock, time zone converter, owned here).
- Personalization: preference profile, admin personalization policy per page, region, setting and audience; add, hide, remove, reorder and resize personal placements; presets; undo and reset; device-specific choices; export; safe suggestions; behavioral personalization only with explicit policy and consent.
- Audience targeting of pages and placements, with rule preview and default fallback.

**Out of scope:**
- Page templates, layouts, builder and component registry (spec 032).
- Brand and token values (spec 033).
- Content feed ranking and follow preferences for news (spec 009 FR-017) and search ranking (spec 012). This spec provides the shared preference profile they read.
- The tenant admin console frame (spec 006 admin shell). The admin shell reuses this spec's states and interaction rules.

## 3. Actors and permissions

| Actor | What they can do in this feature | Permission / scope required |
|---|---|---|
| Employee | Use the shell, set preferences, personalize delegated regions, keep favorites | Tenant membership; `personal_layout.edit` (granted to Employee by default) |
| Organization administrator | Configure navigation, home page and personalization policy for their scope | `navigation.edit`, `personalization.policy` at org or subtree scope; `page.edit` (spec 032) |
| Tenant administrator | Configure shell layout, global navigation, tenant personalization policy, consent policy, help and tours | `shell.edit`, `navigation.edit`, `personalization.policy` at tenant scope |
| Communications administrator | Configure in-context help, tours and global announcements | `help.edit` at tenant or org scope; announcements per spec 009 |
| Security officer | Read configuration history | `config_history.view` |

Permission keys follow spec 004 FR-013. Preferences are never an authorization input (source section 1.3).

## 4. User scenarios

### User Story 1: One consistent shell everywhere (Priority: P1, R1)

Fatima opens the intranet in Arabic on her laptop. The shell shows her subsidiary's logo, the organization she is in, global navigation, search, notifications with a count, her task badge and her profile menu. Every module she opens uses the same buttons, empty states and error messages. When she has no access to a page, she sees a clear message and a way back, never a broken page.

**Why this priority:** the shell is the first thing every user sees and the frame that every other spec depends on.

**Independent test:** render three unrelated component packs inside the shell; run the pattern audit for labels, states and messages; change the shell layout and confirm no page component changed or redeployed.

**Acceptance scenarios:**
1. **Given** a tenant admin moves the search box and adds a utility region to the shell, **When** they publish, **Then** all pages show the new shell without changing or redeploying any page component. *(CX-EXP-001)*
2. **Given** three unrelated component packs, **Then** equivalent intents (save, cancel, delete, retry, filter) use the same labels, controls and feedback, and each pack renders loading, empty, error, permission denied, offline and unsupported states from the shared definitions. *(CX-EXP-002, CX-EXP-006)*
3. **Given** a critical error, **Then** it stays until handled; **Given** a low-priority notice, **Then** it never blocks the task and dismisses itself or on demand. *(CX-EXP-007)*
4. **Given** each reference template, **Then** language, theme mode, density, accessibility options and personalization are reachable from the same place in the shell. *(CX-EXP-010)*
5. **Given** a menu item that points to a page the user cannot see, an unpublished page or a missing page, **Then** the user gets a safe recovery page that does not reveal whether the page exists, with links back. *(CX-NAV-009)*

### User Story 2: Personalize my home within the rules (Priority: P1, R1 add, hide, reorder and reset; R2 resize and presets)

Ahmed's home has a locked top region with critical announcements and a personal region. He enters personalization mode, adds the leave calendar and the world clock, hides the newcomers list, and moves My services to the top, using the keyboard. His coworkers' homes do not change. Later he resets his home to the organization default.

**Independent test:** with a policy that delegates one region, perform add, hide, remove and reorder by pointer and by keyboard; reload on another browser; reset; confirm coworkers and protected regions are unaffected and that the API rejects the same changes on protected regions.

**Acceptance scenarios:**
1. **Given** personalization mode, **Then** edit controls appear only in regions the policy delegates for the user's audience. *(CX-PER-002)*
2. **Given** the add panel, **Then** it lists only registered, enabled, entitled components the user is permitted to use and that the region allows; the added placement is visible to that user only. *(CX-PER-003)*
3. **Given** a protected placement, **When** the user tries to hide or remove it through the UI or the API, **Then** it is refused. *(CX-PER-004, S04)*
4. **Given** a reorder by keyboard (move up, move down, move to region) or by drag, **Then** the result is the same, survives reload and is recalculated at each viewport without invalid order. *(CX-PER-005, S03)*
5. **Given** undo after three changes, **Then** they reverse in order; **Given** reset for this page, **Then** only this page's layout returns to default; **Given** reset all, **Then** all personal layouts reset and other profile data (language, favorites) stays. *(CX-PER-007)*
6. **Given** sign-in on another supported browser, **Then** layout and preferences are restored. *(CX-PER-001)*

### User Story 3: Targeted experiences with safe fallback (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a home variant targeted to "Plant operators" by group and locale, **When** the admin opens targeting preview with representative personas, **Then** it shows who matches, who does not and what each persona sees. *(CX-PER-008)*
2. **Given** a user who matches no variant, **Then** they get the defined default page, never an empty page. *(CX-PER-010)*
3. **Given** a placement hidden from a user by a visibility rule, **When** that user calls the component's API directly, **Then** the API authorizes on its own rules; hiding neither grants nor removes access. *(CX-PER-009)*
4. **Given** collisions between a user choice, an admin default, an audience rule, a parent lock and a legal constraint, **Then** the documented precedence decides and automated tests cover each collision. *(CX-PER-011)*

### User Story 4: Find my way in a large, multi-organization intranet (Priority: P1, R1; R2 favorites, recents and commands)

**Acceptance scenarios:**
1. **Given** global, organization, section and contextual menus, **When** an admin changes one, **Then** the others, the pages and the components are unchanged. *(CX-NAV-001)*
2. **Given** menus, **Then** they work with keyboard, touch and screen readers, show focus clearly and close predictably (Escape, outside click, focus leaving). *(CX-NAV-002)*
3. **Given** a page renamed or moved in the IA, **Then** its components are untouched, the old slug redirects, and breadcrumbs show the logical parents, not browser history. *(CX-NAV-003, CX-NAV-004)*
4. **Given** a user who belongs to two organizations, **When** they switch, **Then** the shell shows which organization or workspace is active, and pages with the same name in each are labelled with their owner. *(CX-NAV-005)*
5. **Given** the quick navigation box, **Then** it lists only destinations the user is permitted to open. *(CX-NAV-006)*
6. **Given** a favorite to a page whose access is later revoked, **Then** the favorite is disabled or removed; the user can clear recents at any time. *(CX-NAV-007)*
7. **Given** a mobile width, **Then** every primary destination is reachable with the same labels. *(CX-NAV-008)*
8. **Given** the command entry point, **Then** it offers only commands and destinations the user can use. *(CX-EXP-008)*

### User Story 5: Interaction standards (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** any interactive view, **Then** it has at most one primary action per task context, and destructive actions look different. *(CX-INT-001)*
2. **Given** a form error, **Then** entered values are kept, the error is linked to the field and announced, and the user corrects it without restarting. *(CX-INT-002)*
3. **Given** an action that takes time, **Then** immediate feedback appears with a non-blocking progress state and an explicit completion or error. *(CX-INT-003)*
4. **Given** a destructive or high-impact action, **Then** it needs explicit confirmation or offers undo per risk policy; a single accidental tap never executes it. *(CX-INT-004)*
5. **Given** a hover interaction, **Then** an equivalent path exists for touch and keyboard. *(CX-INT-005)*
6. **Given** comfortable and compact density, **Then** both keep all required actions and work at 200% zoom. *(CX-INT-007)*
7. **Given** reduced motion set by the user or device, **Then** nonessential animation stops without loss of function. *(CX-INT-008, CX-ACC-010)*
8. **Given** slow data, **Then** skeletons keep the expected region sizes without layout jumps. *(CX-INT-009)*
9. **Given** a bulk selection, **Then** the count and consequence are stated in text before the action, and the selection can be cancelled. *(CX-INT-012)*

### User Story 6: Privacy and control over personalization (Priority: P1, R1 policy and consent model; R3 behavioral personalization)

**Acceptance scenarios:**
1. **Given** a tenant that has not enabled behavioral personalization, **Then** nothing is ranked or suggested from tracked behavior. *(CX-PER-013)*
2. **Given** a tenant that enables it with consent, **When** a user opts out, **Then** profile-based ranking stops and essential settings remain. *(CX-PER-013)*
3. **Given** a suggestion ("pin the services you used most"), **Then** it explains why, needs the user's acceptance, and can be reversed; nothing changes silently. *(CX-PER-015)*
4. **Given** a preference export, **Then** it contains only the user's own preferences in a documented JSON format. *(CX-PER-014)*

### Edge cases and exception flows
- A component in a personal layout is retired or the package loses it: the placement shows "no longer available" with a remove action; the rest of the home is intact.
- An admin removes a region from the delegated policy: personal placements in it are hidden, not deleted, until the policy returns or the user resets.
- The audience of a user changes (moved unit): their personal layout follows the new page's policy; incompatible placements are listed for the user.
- Desktop-only arrangement on a phone: the mobile order is derived and valid; device-specific choices apply only to preferences classified as device-specific. *(CX-PER-012)*
- Help overlay opened on a page with a critical modal: the critical modal wins; help waits.
- The user is offline: the shell shows the offline state and keeps unsaved preference edits locally until reconnection.

## 5. Functional requirements

Shell and states
- **FR-001**: The system MUST provide a configurable global shell with identity, global navigation, contextual actions, breadcrumbs and optional utility regions, independent of page content and changeable without redeploying components. *(CX-EXP-001, EXP-001)*
- **FR-002**: Equivalent user intents MUST use the shared labels, controls, states and feedback of the design system regardless of the component. *(CX-EXP-002)*
- **FR-003**: Every page MUST make its primary purpose and main task clear, with secondary and supporting information visually subordinate. *(CX-EXP-003)*
- **FR-004**: Secondary controls and advanced settings MUST be reachable through progressive disclosure. *(CX-EXP-004)*
- **FR-005**: Navigation between pages, panels and dialogs MUST keep position, filters and unfinished input within documented rules. *(CX-EXP-005)*
- **FR-006**: Loading, success, empty, error, permission denied, offline and unsupported states MUST be centrally defined and renderable by any component. *(CX-EXP-006)*
- **FR-007**: Announcements, confirmations, toasts and dialogs MUST follow priority and dismissal rules: critical persists until handled, low priority never blocks. Critical announcements from spec 009 take priority over all other interruptions. *(CX-EXP-007)*
- **FR-008**: The shell SHOULD offer one permission-aware command entry point for navigation and permitted actions. *(CX-EXP-008, R2)*
- **FR-009**: The shell SHOULD offer a focus or reading mode that keeps content and semantics. *(CX-EXP-009, R2)*
- **FR-010**: Language, theme mode, density, accessibility options and personalization settings MUST be reachable from the same place on every page. *(CX-EXP-010)*

Navigation
- **FR-011**: Global, organization, section and contextual navigation MUST be configured independently of pages and components. *(CX-NAV-001)*
- **FR-012**: Menus MUST support keyboard, touch and screen readers with visible focus and predictable closing. *(CX-NAV-002)*
- **FR-013**: Labels, categories, hierarchy and URL slugs MUST be managed apart from templates; renaming or moving a page MUST keep a redirect from the old slug. *(CX-NAV-003)*
- **FR-014**: Breadcrumbs MUST follow the logical information architecture and give stable parent destinations. *(CX-NAV-004)*
- **FR-015**: The shell MUST show the active organization or workspace when the user can switch between scopes, and label same-named pages by owner. *(CX-NAV-005)*
- **FR-016**: The shell MUST provide search and quick navigation to permitted destinations only (results from spec 012). *(CX-NAV-006)*
- **FR-017**: Users SHOULD keep favorites and see recents, with clear history; revoked access MUST disable or remove affected shortcuts. *(CX-NAV-007, R2)*
- **FR-018**: Menus MUST adapt across devices while keeping function and labels; all primary destinations MUST be reachable at mobile widths. *(CX-NAV-008)* On wide screens the global menu takes the navigation style of the tenant's design (icon rail in Beats, top bar in Atlas, labelled sidebar in Pulse and Studio, spec 036); on screens up to 1024 px wide every style becomes the same menu behind one button.
- **FR-019**: Missing, unpublished and inaccessible destinations MUST show a security-safe recovery path, never an unhandled error. *(CX-NAV-009)*
- **FR-020**: Contextual help, hints, onboarding and guided tours SHOULD be configurable by policy, dismissible and reopenable without blocking overlays. *(CX-NAV-010, R2)*

Personalization and targeting
- **FR-021**: Users MUST have a persisted, cross-device preference profile for eligible appearance, layout, navigation and accessibility options. *(CX-PER-001)*
- **FR-022**: Admins MUST define per page, region, setting and audience what users may personalize. *(CX-PER-002)*
- **FR-023**: Users MUST add permitted registered components to delegated regions, visible only to themselves unless explicitly shared. *(CX-PER-003)*
- **FR-024**: Users MUST hide or remove eligible placements; protected placements MUST NOT be removable, enforced by the API. *(CX-PER-004)*
- **FR-025**: Users MUST reorder personal placements, and resize them within constraints from R2, by pointer and by keyboard alternatives; changes MUST survive reload and responsive changes. *(CX-PER-005)*
- **FR-026**: The system SHOULD offer personal layout presets (default, compact, focus) applied in one action and resettable, without changing entitlements. *(CX-PER-006, R2)*
- **FR-027**: Users MUST undo recent changes and reset one page or all pages to the organization default, without touching other profile data. *(CX-PER-007)*
- **FR-028**: Admins MUST target pages and placements by role, group, org unit, locale and approved attributes, with a persona preview of eligibility and fallbacks. *(CX-PER-008)*
- **FR-029**: Visibility rules MUST be evaluated separately from component data authorization. *(CX-PER-009, spec 004 FR-006)*
- **FR-030**: Every page with audience variants MUST have a default for users who match none. *(CX-PER-010)*
- **FR-031**: Precedence MUST be: legal and security constraints and parent locks, then audience rules, then admin defaults, then user choices where allowed; it MUST be documented and covered by automated collision tests. *(CX-PER-011)*
- **FR-032**: Each preference MUST be classified as cross-device or device-specific; device-specific choices SHOULD be supported and MUST NOT create invalid order on other devices. *(CX-PER-012)*
- **FR-033**: Behavioral personalization MUST be off unless the tenant enables it by explicit policy, and MUST require user consent where applicable, with opt-out that keeps essential settings. *(CX-PER-013)*
- **FR-034**: Users SHOULD export and reset their own preferences in a documented format that contains no other user's or organization's data. *(CX-PER-014)*
- **FR-035**: Suggestions SHOULD explain why they are offered, need acceptance and be reversible; the system MUST NOT silently change persistent preferences. *(CX-PER-015)*
- **FR-036**: Preference records MUST hold user reference, resolved scope, schema version, allowed overrides, optional device class and last-modified time, and MUST NOT hold component business data, tokens or cross-tenant content. *(source section 7.3)*

Interaction standards
- **FR-037**: Each view MUST distinguish primary, secondary and destructive actions. *(CX-INT-001)*
- **FR-038**: Forms MUST validate clearly, keep entered values and link errors to fields with accessible announcements. *(CX-INT-002)*
- **FR-039**: Actions MUST give immediate feedback and explicit completion or error states; slow actions MUST show non-blocking progress. *(CX-INT-003)*
- **FR-040**: Destructive or high-impact actions MUST require confirmation or offer undo according to risk policy. *(CX-INT-004)*
- **FR-041**: Hover, click, tap, swipe and keyboard behaviors MUST be consistent, and every hover-only path MUST have touch and keyboard equivalents. *(CX-INT-005)*
- **FR-042**: System labels and messages MUST be concise, plain-language, active, translatable and free of developer jargon. *(CX-INT-006)*
- **FR-043**: Comfortable and compact density presets MUST exist without hiding required actions. *(CX-INT-007)*
- **FR-044**: Motion MUST clarify state changes and respect reduced-motion preferences. *(CX-INT-008)*
- **FR-045**: Loading skeletons MUST match the expected regions and avoid layout shift. *(CX-INT-009)*
- **FR-046**: Complex tasks SHOULD offer inline hints, examples or explanations. *(CX-INT-010)*
- **FR-047**: Interactive targets SHOULD be at least 44 by 44 CSS pixels where practical and never below the WCAG 2.2 AA minimum of 24 by 24. *(CX-INT-011)*
- **FR-048**: Selection and bulk actions SHOULD state the selected count and consequence in text and allow cancel. *(CX-INT-012)*

Employee home
- **FR-049**: The employee home MUST be a page of spec 032 with at least one protected region for mandatory and critical items and at least one delegated personal region by default. *(EXP-001)*
- **FR-050**: The tool box utilities (weather, world clock, time zone converter) MUST be registered as platform components through spec 032 manifests; other home components belong to their modules. *(EXP-001)*
- **FR-051**: When the tenant turns on Microsoft 365 Mail and Calendar (spec 018), the default home template MUST include My calendar and My mail in the personal region for employees with a mailbox, and the shell MAY show an unread mail badge in a utility region. Employees can remove, reorder or re-add them like any personal placement. *(EXP-001, INT-002)*
- **FR-052**: The default home MUST open with the briefing hero (`home.briefing`, a platform component) at the top start of the page and Your day (spec 016 task summary) beside it. The briefing greets the viewer with the date and, when a location is set, the weather, and lists the next three things that need the viewer, ordered by time: meetings still to come today (spec 018), tasks in the inbox (spec 016) and policies waiting to be accepted (spec 025), each with its main action. Every source is read with the viewer's own session; a source that fails or that the tenant does not have is left out, never guessed. In R1 the wording is fixed text; the AI written briefing of spec 020 replaces it in R2. *(EXP-001)*
- **FR-053**: Campaign and call to action bands MUST close the home, below the personal and news regions and above the end of the page, never above the briefing. They align to the page frame like every other section and never touch the navigation. *(EXP-001)*
- **FR-054**: Featured news on the home MUST show the lead story as a large card with its reading time and reaction count, and the next stories as compact rows (spec 009 FR-021). *(EXP-001)*
- **FR-055**: Home cards for features not yet built (recognition, celebrations, polls, communities, leave balance) MUST NOT be drawn. Their place in the home template is a planned slot that stays hidden until the owning module registers its component. *(EXP-001)*
- **FR-056**: The shell MUST show an Ask TeamBeats button at the bottom end of every page that opens the assistant (spec 030), only when the tenant's package has the assistant (the shell API says so; the web app checks no package). The shell MUST show a footer under every page with the organization name, the tenant's privacy, acceptable use and accessibility links (and other links it names) and the classification, drawn only from the shell configuration (at most 8 links; a label per locale of at most 80 characters; a link goes to a path of this intranet or to an https address, never another scheme or a `//` or `\\` address) and absent while the tenant configured none. *(EXP-001)*
- **FR-057**: Your day (`home.your_day`, a platform component) MUST show tasks to approve, overdue tasks and meetings left today as tinted counts, then today's agenda from the viewer's calendar (spec 018) with a "starts in" badge for the next hour. When the calendar is not connected it shows one line with the connect action; when it cannot be read it says so; it never guesses meetings. *(EXP-001)*
- **FR-058**: The home MUST offer more views as tabs after My day (for example Team lead, Plant floor), configured in the shell as up to 5 views, each a short id, a label per locale, a published page of the tenant other than the home and an optional audience rule (FR-030). A view needs a home page. A viewer gets the tab only when they may view its page and its rule, if any, matches them; a rule needs the `experience.audience_variants` capability, and without it a targeted view is refused on save and shown to nobody. A viewer with no extra view sees no tabs. The shell API sends each viewer only their own views (id, label, page slug), never the pages and rules behind them. The tabs are links (`/?view=<id>`), and an id the viewer does not get opens My day. *(EXP-001)*
- **FR-059**: The control that starts personalization MUST be an icon button (the edit icon) in the shell bar next to settings, named "Arrange this page" for assistive technology and in its tooltip, and shown as pressed while arranging. It takes no row of its own in the page. While arranging, a toolbar with Done, Undo and Reset sits at the top end of the page frame. Pages without anything the viewer may arrange show no icon. *(EXP-001)*
- **FR-060**: Only one shell bar panel (settings, notifications, account) MAY be open at a time: opening one closes any other, a press outside a panel closes it, and Escape closes it and returns focus to its button. *(EXP-001, CX-ACC-002)*
- **FR-061**: In personalization mode the user MUST be able to move an item by dragging its handle, within its area or into another area that takes items, with mouse, pen or touch. A mark shows where it will land, and the item being moved fades. Move up, move down and move to another area stay as buttons, so every move also works from the keyboard (FR-025, spec 007). The API checks a dragged move like any other. *(EXP-001, CX-PER-005)*
- **FR-062**: The account menu MUST offer an Administration row, between Preferences and Sign out, to people who may open at least one admin shell section (spec 006 FR-008), on every page and whatever page is set as home. The shell API says whether to show it, using the same checks that build the admin navigation, so the web app checks no role; employees and contractors without a section never see it. The row is only a way in: every admin section still authorizes itself. *(EXP-001, ADM-001)*

## 6. Business and validation rules
- Preferences never grant access, never hide mandatory or critical content, and never change entitlements.
- Default personalization policy for new tenants: home page personal region delegated to all employees; all other pages not personalizable.
- Undo history keeps the last 20 personal layout changes per page for the session.
- A personal region holds at most 12 personal placements by default.
- Recents keep 50 entries for 90 days by default; users can clear them.
- Behavioral personalization requires a tenant decision recorded in the audit log and, where the tenant's privacy policy says so, a user consent record.

## 7. Tenant and organization scope
- Tenant-owned data: shell configuration, navigation, personalization policies, help content, preference profiles, favorites, recents.
- Organization scoping: navigation and policies at org or subtree scope; preference profile per user per tenant membership.
- Cross-tenant denial cases: read or write another tenant's preference profile; quick navigation returning another tenant's pages; a favorite or recent from tenant A shown after switching to tenant B.
- Sibling, parent and child cases: a sibling org admin cannot change another's navigation; a child cannot remove parent-locked shell items; a user moved between units keeps only preferences valid in the new scope.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Shell and navigation configuration | Product | Internal | Life of tenant, releases 2 years | No |
| Personalization policy | Product | Internal | Life of tenant | No |
| Preference profile and personal layouts | Product | Internal | Life of membership; deleted 30 days after membership ends | Yes |
| Favorites and recents | Product | Internal | Recents 90 days; favorites life of membership | Yes |
| Behavioral signals (only if enabled) | Product | Confidential | 90 days rolling | Yes |
| Consent records | Product | Internal | Life of membership plus 2 years | Yes |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `experience.personalization` (personal regions on pages beyond home, presets, device-specific choices) | off (home only) | on | on |
| `experience.audience_variants` (targeted page variants) | off | on | on |
| `experience.guided_tours` | off | on | on |
| `experience.behavioral_personalization` (R3, with consent) | off | off | on |

The shell, shared states, navigation, quick navigation, favorites, home personalization, preference profile and interaction standards are in every package.

## 10. UX, localization and accessibility
- Personalization mode is a clear mode with a visible exit, an announced state for screen readers, and action menus on each placement.
- All shell and menu patterns work mirrored in RTL; icons with direction are mirrored.
- Organization indicator uses text, not color alone.
- Help and tours never trap focus and can be closed with Escape.

## 11. AI behavior and data policy
- R1 and R2 suggestions (FR-035) are rule-based. If a later release uses a model, it goes through the AI Gateway (spec 013), uses only the user's own signals and permitted items, and follows FR-033.
- The command entry point can hand free text to the AI assistant (spec 014); the assistant applies its own permissions.

## 12. Integrations and events
- Publishes: `ShellPublished`, `NavigationPublished`, `PreferencesChanged`, `PersonalLayoutReset`.
- Consumes: `UserLocaleChanged` (spec 007), `OrganizationMembershipChanged` (spec 002), `EntitlementsChanged` (spec 005), `ComponentRetired` (spec 032), `RoleAssignmentChanged` and `ResourcePermissionChanged` (spec 004, to prune favorites).

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| shell.published / navigation.published | audit | scope, version, actor, diff reference |
| personalization.policy_changed | audit | scope, page, region, audience, old and new |
| experience.behavioral_personalization_changed | audit | tenant, actor, consent mode |
| preference.exported | audit | user |
| personal_layout.changed / reset | analytics | page key, action, pseudonymous user (spec 021) |
| navigation.used, quick_nav.searched | analytics | destination key, success, channel |

## 14. Security and abuse cases
- Using a personal placement to call an API the user lacks: every component API authorizes server side.
- Enumerating pages through quick navigation or breadcrumbs: results and parents are filtered by permission; recovery pages do not confirm existence.
- Stored script in personal settings (custom link names): text only, sanitized, length limited.
- Preference export used to pull other users' data: export is built from the requester's own records only.
- Recents and favorites leaking after tenant switch: stored per tenant membership, cache keys include tenant (spec 032 FR-065).

## 15. Non-functional requirements
- Shell adds no more than 50 KB compressed of script to first load; shell render under 100 ms on the reference device after data arrives.
- Preference profile read p95 under 20 ms from cache; personal layout saves under 300 ms p95.
- Quick navigation results under 300 ms p95 (search spec 012 budget applies to full search).

## 16. Success criteria
- **SC-001**: Pattern audit across three unrelated component packs finds no conflicting action conventions.
- **SC-002**: A user reorders their personal region and coworkers' default layouts are unchanged (S03).
- **SC-003**: Employees and child admins cannot remove a locked region through UI or API (S04).
- **SC-004**: In pilot usability tests, 90% of first-time users find a named service page within 60 seconds.

## 17. Open questions
- [NEEDS CLARIFICATION: should employees be able to personalize pages other than home in Workplace?] Default: home only in Workplace.
- [NEEDS CLARIFICATION: is behavioral personalization wanted at all?] Default: off, R3, Enterprise & Government only, consent required.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| CX-EXP-001 | FR-001, US1 |
| CX-EXP-002 | FR-002, US1 |
| CX-EXP-003 | FR-003 |
| CX-EXP-004 | FR-004 |
| CX-EXP-005 | FR-005 |
| CX-EXP-006 | FR-006, US1 |
| CX-EXP-007 | FR-007, US1 |
| CX-EXP-008 | FR-008, US4 |
| CX-EXP-009 | FR-009 |
| CX-EXP-010 | FR-010, US1 |
| CX-NAV-001 | FR-011, US4 |
| CX-NAV-002 | FR-012, US4 |
| CX-NAV-003 | FR-013, US4 |
| CX-NAV-004 | FR-014, US4 |
| CX-NAV-005 | FR-015, US4 |
| CX-NAV-006 | FR-016, US4 |
| CX-NAV-007 | FR-017, US4 |
| CX-NAV-008 | FR-018, US4 |
| CX-NAV-009 | FR-019, US1 |
| CX-NAV-010 | FR-020 |
| CX-PER-001 | FR-021, US2 |
| CX-PER-002 | FR-022, US2 |
| CX-PER-003 | FR-023, US2 |
| CX-PER-004 | FR-024, US2 |
| CX-PER-005 | FR-025, US2 |
| CX-PER-006 | FR-026 |
| CX-PER-007 | FR-027, US2 |
| CX-PER-008 | FR-028, US3 |
| CX-PER-009 | FR-029, US3 |
| CX-PER-010 | FR-030, US3 |
| CX-PER-011 | FR-031, US3 |
| CX-PER-012 | FR-032 |
| CX-PER-013 | FR-033, US6 |
| CX-PER-014 | FR-034, US6 |
| CX-PER-015 | FR-035, US6 |
| CX-INT-001 | FR-037, US5 |
| CX-INT-002 | FR-038, US5 |
| CX-INT-003 | FR-039, US5 |
| CX-INT-004 | FR-040, US5 |
| CX-INT-005 | FR-041, US5 |
| CX-INT-006 | FR-042 |
| CX-INT-007 | FR-043, US5 |
| CX-INT-008 | FR-044, US5 |
| CX-INT-009 | FR-045, US5 |
| CX-INT-010 | FR-046 |
| CX-INT-011 | FR-047 |
| CX-INT-012 | FR-048, US5 |
| EXP-001 | FR-001, FR-049, FR-050, FR-051 |
| INT-002 (home components) | FR-051, spec 018 |
| EXP-008 | FR-012, FR-018 (with spec 007) |

## Review checklist
- [x] No implementation details
- [x] Every FR is testable and traces to a requirement ID
- [x] Cross-tenant and org boundary cases are listed
- [x] Entitlement behavior is defined
- [x] Audit events are defined
- [ ] No open [NEEDS CLARIFICATION] items that change scope
