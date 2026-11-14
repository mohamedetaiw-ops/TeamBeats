# Tasks: TeamBeats Design Language System

Depends on 033 (theme engine, brand manager), 008 (shell), 032 (templates), 007. R1.

## Phase 1: Designs in the theme engine
- [x] T001 Choice token type, `design.preset` and `layout.navigation`, approved fonts for the designs
- [x] T002 Atlas and Pulse presets; resolver inserts the effective preset with locks; schema endpoint lists choices and design previews
- [x] T003 [P] Tests: every design valid and passing strict contrast in light and dark; designs distinct; Beats equals the platform default; brand wins; child choice and lock; unknown choice refused; unknown older value falls back

## Phase 2: Web app
- [x] T004 Root layout sets `data-design` and `data-nav`; fonts for Atlas and Pulse; navigation styles for top bar and sidebar; `designs.css`
- [x] T005 Components: Card, Badge, Avatar, Alert, Media, Tabs, TextAreaField, SelectField; 20 new icons with automatic mirroring
- [x] T006 Gallery: colour, type, icons, actions, text boxes, status, cards, imagery, tabs and templates in the active design
- [x] T007 Design picker in the brand manager with live miniatures; layout group in the token editor

## Phase 3: Enforcement and testing
- [x] T008 Design consistency gate `npm run check:design` in `verify`
- [x] T009 Design suite: axe serious and critical, reflow at three widths, target size, visual baselines, review screenshots
- [x] T010 Guidelines in `docs/design-system`; constitution XXXI; spec template; CLAUDE.md; references in specs 007, 008 and 033; ADR-0021

## Phase 4: Studio and the extended catalogue (DLS-030 to DLS-041)
- [x] T014 Studio preset, Inter font, picker entry, Studio rules in `designs.css`, unit and design suite coverage in four designs
- [x] T015 Actions, selection and status: Button sizes, icons and link form, ButtonGroup, IconButton, CloseButton, Checkbox, RadioGroup, Switch, card forms, SegmentedControl, Slider, Chip, TagGroup, StatusDot, Kbd, Divider, Spinner, Progress, Meter, AvatarGroup, Pagination, Accordion, Announcement; second gallery page `/ui-gallery/components` in the design suite; `components.spec.ts`
- [x] T016 Overlays and menus: Dialog, Drawer, Popover, Tooltip, Menu with groups, checked items and submenus, ContextMenu, notification card form of Messages, CommandPalette opened by the shell search shortcut, Breadcrumb with collapsed levels; shared placement and list keyboard helpers in `overlay.ts`; gallery sections and `components.spec.ts` coverage
- [x] T017 Inputs and dates: field parts, SearchField, NumberField, OtpField, Combobox, Calendar, DateField, DateRangeField, TimeField, SlotPicker, ColorField, FileUpload, Carousel, TreeView
- [x] T018 Data blocks: DataTable, DataGrid, StatCards, calendar views, task list, notification panel, settings dialog, sign-in card
- [x] T019 Assistant and agent parts: thinking indicator, composer, attachments, sources, limits, question steps, agent form, run progress
- [x] T020 Charts: chart tokens in the schema and four designs, chart card, the chart types of DLS-040
- [x] T021 Page patterns: dashboard, assistant chat, agent profile, record profile, board, mapped to the spec 032 templates

## Phase 5: Detail quality (DLS-042 to DLS-055)
- [x] T022 Detail checklist `docs/design-system/foundations/details.md` and the review of the staging screens in `docs/review/ui-detail-review.md`
- [x] T023 Global details: value formatters (`lib/format.ts`), link and breadcrumb rules, native radio and checkbox look, hints under controls, filled selected chips, status badges, empty states with icons, space for Ask TeamBeats
- [x] T024 Shell panels on one popover surface: account menu rows, notification rows with relative time and category icons, grouped settings with segmented controls
- [x] T025 Pages: Your day empty state, request rows, person cards, own profile values and tags, knowledge article header and feedback toggles, documents filter bar, sort direction and folder chips, service form column
- [ ] T026 Move the shell panels onto the T016 popover and T018 notification panel and settings blocks once they merge, keeping the DLS-045 to DLS-047 look

## Phase 6: Calendar parts (DLS-069 to DLS-074, spec 037)
- [ ] T027 Calendar header, layer chips, week strip, agenda rows, month with ranges, day panel and bottom sheet, availability stats and heatmap in `components/ui`, gallery section `cg-calendar`, keyboard coverage

## Phase 7: Designs on every page (DLS-075)
- [x] T028 Home widgets on catalogue blocks (Your day and Your tasks on `StatCards` with tones, My mail view switch on `SegmentedControl`, unread count and markers as `Badge`, agenda "starts in" as `Badge`); Studio finish for page cards, tiles, section tabs, icon boxes, stat cards, briefing, lead story and page headers in `designs.css`

## R2
- [ ] T011 Design review checklist in the spec 033 theme draft and approval flow
- [ ] T012 Firefox and WebKit in the design suite after the browser matrix decision
- [ ] T013 Quarterly design audit report from the gallery and the spec 033 drift report

## Phase 8: UX audit of 13 November 2026 (DLS-078 to DLS-088 and the linked requirements)
Findings TB-01 to TB-20 of the staging audit, in the audit's execution order.
- [x] T029 Critical fixes: update offer only for a newer release, docked in the page flow, Later kept for the session (spec 030 FR-002, DLS-078, TB-02); approval notifications resolved when their task closes (spec 015 FR-025, TB-06); no raw keys and the library scope label (spec 007 FR-030, spec 011 FR-034, TB-01)
- [x] T030 Shared parts: stacked card mode and column priority in `DataTable` for the policy register and admin People (DLS-079, spec 025 FR-019, spec 006 FR-019, TB-03, TB-16); scrolling `SectionNav` with edge fades (DLS-080, TB-04); Ask TeamBeats in the bottom safe area (DLS-066, spec 008 FR-066, TB-11); one row phone shell bar (spec 008 FR-063, TB-05)
- [x] T031 Usability: naming pass and the Services tabs (spec 008 FR-064, spec 016 FR-017, TB-09); assistant delete all in a menu with a counted confirmation (DLS-084, spec 014 FR-023, TB-13); grouped admin navigation and the admin overview (spec 006 FR-017, FR-018, TB-15); directory unit names, New joiners and title fallback (spec 010 FR-023, FR-024, TB-07, TB-08); search and filters in the address on People and Services; Policies already used the address (spec 008 FR-065, TB-19)
- [x] T032 Visualisation: weekly active people and unit charts (spec 021 FR-025, TB-16); service card time, approver and last request (spec 016 FR-019)
- [x] T033 Polish: one icon per service (DLS-081, spec 016 FR-018, TB-12); dates with month names and the date field readout (DLS-082, TB-17); field specific validation and error summary (DLS-083, TB-20); 44px touch targets (DLS-085, TB-18); auto fit stat grids (DLS-086, TB-14); empty state next steps (DLS-087); no spark watermark or stock illustration (DLS-088); seeded news title without a relative time (spec 009 FR-022, TB-10)
- [x] T034 Search and filters in the address on the document library browser; the request lists already used the address (spec 008 FR-065)

## Phase 9: Experience system (DLS-100 to DLS-119)
From the UX, DLS and frontend enhancement report of 14 November 2026. Foundations first, then pages.
- [x] T038 Constitution XXXI 1.3.0, DLS-100 to DLS-119, linked requirements in specs 008, 009, 010, 011, 014, 015, 016, 025, 032, 033 and 037, guideline pages (composition, layout, typography, templates, designs, testing, consistency)
- [x] T039 Type and frame foundation: 12 px floor in every density (`Tokens.cs`, theme check), native controls inherit the font, footer at the end of the window, one page header contract on every page, no one-level breadcrumb, soft current navigation item in every design, gate rules for the page header and literal sizes (DLS-104, DLS-105, DLS-118)
- [x] T040 Filter toolbar part with applied filter chips; one tab style and one tag shape; People, Policies, Requests and Services on the toolbar, Documents with its page in T047 (DLS-106)
- [ ] T041 Content budgets and View all in `Widget`, one instance per source in the renderer and the builder, placement states in the frame (DLS-101 to DLS-103, spec 032 FR-071 to FR-073)
- [x] T042 Tasks: row actions, type icons, review side panel, delegation as a guided dialog; the four inbox tabs and Undo wait for the API (spec 016 FR-020) (DLS-108, DLS-115, spec 016)
- [x] T043 Calendar: agenda grouping, week overlaps, month overflow, one agenda, home summary in T041 with the widget frame (DLS-119, spec 037)
- [x] T044 Assistant workspace on the assistant parts with tools as composer modes and the cannot verify state (DLS-114, spec 014); searching and renaming past conversations and starters drawn from the person's own services wait for the API (spec 014 FR-024)
- [ ] T045 Home views: first viewport contract, Team lead and Plant floor as persona views, no repeated counts, news lead without a placeholder (DLS-100, DLS-102, spec 008)
- [x] T046 Services marketplace layout and My requests progress (DLS-063, DLS-107, spec 016); the step by step progress line on My requests waits for the request summary to carry its steps
- [ ] T047 People toolbar, person forms and quick actions; policies summary and uniform status; knowledge and documents search first; editorial article, reactions and comments (DLS-109, DLS-112, DLS-113, specs 009, 010, 011, 025)
- [ ] T048 Design descriptors and variant tokens in the four designs, variants on the news lead, stat tiles, work rows, tabs, cards and briefing (DLS-110, spec 033 FR-032)
- [ ] T049 Design suite on the main pages in every design, language and density with the high-volume seed; gallery state coverage; visualization rules on stat and chart parts (DLS-111, DLS-117, DLS-118)
