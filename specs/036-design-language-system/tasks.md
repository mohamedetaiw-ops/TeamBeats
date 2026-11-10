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

## R2
- [ ] T011 Design review checklist in the spec 033 theme draft and approval flow
- [ ] T012 Firefox and WebKit in the design suite after the browser matrix decision
- [ ] T013 Quarterly design audit report from the gallery and the spec 033 drift report
