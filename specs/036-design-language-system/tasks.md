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
