# UI testing strategy

A website succeeds through how well it works for people, not only how it looks. Testing the UI is shared work: designers test that the visual and interactive design meets people's needs before and after it is built; developers test that it is technically sound, fast, secure and works everywhere. This page sets who tests what, when, and which checks run automatically.

## When testing happens

| Stage | Who | Tests | Gate |
|---|---|---|---|
| 1. Discovery | UX research | User needs, content strategy, journey maps | Research summary in the spec |
| 2. Concept and wireframes | Design, UX writing | Concept tests, first-click and tree tests, content drafts in both languages | Spec UX section approved |
| 3. High fidelity design | Design, graphics | Usability tests, accessibility review, content review, responsive and RTL review | Design review checklist below |
| 4. Development | Developers | Unit, component, e2e, axe, visual, responsive, performance, security | CI green |
| 5. Post-development review | Design and development together | Fidelity, interaction, content in place, consistency | Designer sign-off on the pull request or staging |
| 6. Release and after | Product, research | Analytics, feedback, accessibility audit, usability with people of determination | Release checklist |

## For UX researchers, designers and the graphics team

Testing the design before it goes to development is the cheapest place to find problems.

### User-centric testing
- Recruit 5 to 8 real employees per round, across roles, ages, Arabic and English speakers, desk and frontline workers, and people of determination.
- Test real tasks ("request two days of leave", "find the travel policy"), with think-aloud, on the device they really use.
- Measure task success, time on task, errors and a short satisfaction score (for example SUS). Record findings with severity and the change made.
- Re-test changed designs. Keep findings with the spec so the next team learns from them.

### Accessibility tests (design perspective)
- Check every colour pair with the brand editor's contrast table or a contrast checker; meaning never by colour alone.
- Annotate heading levels, landmarks, reading and focus order, accessible names and alt text on the design.
- Check targets (44 px for buttons and menu items), visible focus states, text at 200 percent, and reduced motion alternatives.
- Review the design in Arabic right to left, not only mirrored by a tool.

### Test the conversation and the content
- Never design with lorem ipsum. UX writing works in parallel with design and supplies real copy in Arabic and English.
- Read every screen aloud: is it conversational, professional and clear? Does every button say what happens? Do errors say how to fix them?
- Check content fits: the longest realistic Arabic and English strings, names and numbers, plus pseudo-localized text (spec 007).
- A native Arabic reviewer approves Arabic copy.

### Responsive and mobile layout
- Visualise every screen at phone (375 px), tablet (768 px) and desktop (1440 px), in both directions, light and dark.
- Check the three designs (Beats, Atlas, Pulse) for any screen that uses navigation, cards, forms or brand colour.
- Check touch: thumb reach for primary actions on phones, no hover-only behaviour.

### Design review checklist (before handover)
- [ ] Uses only system components and templates, or documents why a new one is needed
- [ ] Real content in Arabic and English, reviewed
- [ ] All states designed: default, hover, focus, active, disabled or busy, error, empty, loading, denied
- [ ] Annotated for accessibility; contrast checked
- [ ] Phone, tablet and desktop; RTL and LTR; light and dark; three designs where relevant
- [ ] Usability tested, findings addressed

## For developers

Developers validate that the site is technically sound: smooth navigation, quick loading, cross-platform compatibility, security, protection of user data and correct dynamic content.

| Area | What | How (automated where possible) |
|---|---|---|
| Functional | Journeys work end to end | Playwright e2e per spec (`tests/Acceptance`), API integration tests |
| Navigation | Links, menus, breadcrumbs, redirects, keyboard | `shell.spec.ts`, `shell-reachability.spec.ts` |
| Accessibility | WCAG 2.2 AA | axe on every primary page in both languages (`expectAccessible`); design suite with zero serious and critical violations in all three designs |
| Responsive | No horizontal scroll, layouts reflow | Design suite at 375, 768 and 1440 px; shell tests at mobile width |
| Target size | Controls at least 24 by 24 px | Design suite on the component gallery |
| Visual regression | Components look as designed in every design and direction | `toHaveScreenshot` baselines in the design suite |
| Consistency | Tokens, icons, images, type, messages | `npm run verify` gates ([consistency](technical/consistency.md)) |
| Theme accessibility | Every design and brand passes contrast | `ThemeEngineTests`, `DesignPresetTests`, the brand editor's blocking checks |
| Performance | LCP, INP, CLS, theme size | `performance.spec.ts`, Web Vitals in staging, CSS size test |
| Security | CSP, rendering, tenant isolation | CSP guard on every e2e test, `secure-rendering.spec.ts`, cross-tenant suite |
| Cross-browser | Chromium in CI on every pull request; Firefox, Safari and mobile Safari before each release | Manual pass on the release checklist until the browser matrix decision in `docs/testing/e2e-plan.md` |
| Dynamic content | Empty, long, Arabic, mixed direction and failing data | Fixtures with long strings, failure injection per placement (spec 032) |

## Post-development test (design and development together)

After the first frontend build of a feature, the designer and the developer review it together on staging or a local build, with an open mind on both sides.

1. Run the design suite with screenshots: `DESIGN_SCREENSHOTS_DIR=../../design-review npx playwright test specs/design-system.spec.ts` writes the home and gallery in every design, language and viewport.
2. Compare against the approved design: spacing, type, colour, states, motion, content and alt text.
3. Walk the journey by keyboard and with a screen reader in Arabic and English.
4. Log each difference as fix now, fix later (ticket) or accept (design updated). The designer signs off on the pull request.

## Consistency checks

- Automated gates on every pull request (see [consistency](technical/consistency.md)).
- A quarterly audit of live screens against the gallery, to catch drift the gates cannot see (wording, patterns used in the wrong place).
- The spec 033 drift report (R2) lists tenant brand values that fail contrast or conflict.

## Release checklist (UI)

- [ ] Design suite, e2e suite and axe green
- [ ] Screenshots of changed screens in all three designs reviewed
- [ ] Manual keyboard and screen reader pass on primary journeys in Arabic and English
- [ ] Cross-browser pass (Chrome, Edge, Firefox, Safari, iOS Safari, Android Chrome)
- [ ] Usability findings from this release addressed or ticketed
