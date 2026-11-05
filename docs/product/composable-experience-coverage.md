# Composable Intranet Experience coverage map

Every requirement of the [Composable Intranet Experience requirements](composable-experience-requirements.md) (182 IDs: 151 Must, 29 Should, 2 Could) with the spec and functional requirements that cover it. IDs carry a `CX-` prefix in the specs. Release per item is in each spec's Target release field: Musts and Shoulds are in by R2 GA, the two Coulds are R3.

| ID | Requirement | Priority | Covered by (spec: FR, user story) |
|---|---|---|---|
| CX-EXP-001 | Shared experience shell | Must | 008: FR-001, US1 |
| CX-EXP-002 | Consistent actions and patterns | Must | 008: FR-002, US1 |
| CX-EXP-003 | Visual hierarchy | Must | 008: FR-003 |
| CX-EXP-004 | Progressive disclosure | Must | 008: FR-004 |
| CX-EXP-005 | Context preservation | Must | 008: FR-005 |
| CX-EXP-006 | Reusable experience states | Must | 008: FR-006, US1 |
| CX-EXP-007 | Interruptive messaging | Must | 008: FR-007, US1 |
| CX-EXP-008 | Context-aware commands | Should | 008: FR-008, US4 |
| CX-EXP-009 | Focus and reading mode | Should | 008: FR-009 |
| CX-EXP-010 | Universal preference access | Must | 008: FR-010, US1 |
| CX-LAY-001 | Declarative page structure | Must | 032: FR-001, US1 |
| CX-LAY-002 | Responsive grid | Must | 032: FR-004, US3 |
| CX-LAY-003 | Container-aware behavior | Must | 032: FR-005, US1 |
| CX-LAY-004 | Minimum layout catalog | Must | 032: FR-006 |
| CX-LAY-005 | Resizable regions | Should | 032: FR-007 |
| CX-LAY-006 | Full-width and constrained sections | Must | 032: FR-008, US3 |
| CX-LAY-007 | Responsive ordering | Must | 032: FR-009, US3 |
| CX-LAY-008 | Logical content order | Must | 032: FR-009, US3 |
| CX-LAY-009 | Layout safeguards | Must | 032: FR-010, US3 |
| CX-LAY-010 | Page safe area | Must | 032: FR-011, US3 |
| CX-LAY-011 | Slot constraints | Must | 032: FR-012, US1 |
| CX-LAY-012 | Responsive fallback | Must | 032: FR-013, US3 |
| CX-TMP-001 | Template independence | Must | 032: FR-014 |
| CX-TMP-002 | Initial template catalog | Must | 032: FR-015, US5 |
| CX-TMP-003 | Initial layout catalog | Must | 032: FR-006, US5 |
| CX-TMP-004 | Design variants | Must | 032: FR-016, US5 |
| CX-TMP-005 | Template preview | Must | 032: FR-017, US5 |
| CX-TMP-006 | Template instantiation | Must | 032: FR-018, US1, US5 |
| CX-TMP-007 | Template lock regions | Must | 032: FR-019, US5 |
| CX-TMP-008 | Template distribution | Must | 032: FR-020, US5 |
| CX-TMP-009 | Template lifecycle | Must | 032: FR-021, US5 |
| CX-TMP-010 | Template import export | Should | 032: FR-022 |
| CX-TMP-011 | Starter configuration | Must | 033: FR-027, US6 |
| CX-TMP-012 | Template recommendations | Could | 032: FR-023 |
| CX-BRD-001 | Tenant brand profiles | Must | 033: FR-001, US1 |
| CX-BRD-002 | Organization brand overrides | Must | 033: FR-002, US1 |
| CX-BRD-003 | Token-driven theming | Must | 033: FR-003, US1 |
| CX-BRD-004 | Token inheritance | Must | 033: FR-004, US1 |
| CX-BRD-005 | Brand asset management | Must | 033: FR-005, US3 |
| CX-BRD-006 | Multiple brand variants | Must | 033: FR-006, US2 |
| CX-BRD-007 | Typography policy | Must | 033: FR-007, US3 |
| CX-BRD-008 | Preview before publishing | Must | 033: FR-008, US2 |
| CX-BRD-009 | Brand safety validation | Must | 033: FR-009, US2 |
| CX-BRD-010 | Custom domain and identity | Should | 033: FR-010, US7 |
| CX-BRD-011 | Branded loading and errors | Must | 033: FR-011, US3 |
| CX-BRD-012 | Theme publishing and rollback | Must | 033: FR-012, US2 |
| CX-BRD-013 | Scoped custom styling | Should | 033: FR-013, US8 |
| CX-BRD-014 | Exportable design tokens | Should | 033: FR-014, US8 |
| CX-BRD-015 | Brand drift reporting | Should | 033: FR-015, US8 |
| CX-ORG-001 | Scope model | Must | 033: FR-016, US4 |
| CX-ORG-002 | Policy and visual separation | Must | 004: FR-004, FR-006 (with spec 033 FR-017); 033: FR-017, US4 |
| CX-ORG-003 | Effective configuration resolution | Must | 033: FR-018, US1 |
| CX-ORG-004 | Protected parent settings | Must | 033: FR-019, US1 |
| CX-ORG-005 | Delegated administrators | Must | 033: FR-020, US4 |
| CX-ORG-006 | Scoped page catalogs | Must | 033: FR-021, US4 |
| CX-ORG-007 | Cross-organization journeys | Should | 033: FR-022, US4 |
| CX-ORG-008 | Preview as organization | Must | 033: FR-023 |
| CX-ORG-009 | Change impact analysis | Must | 033: FR-024, US2 |
| CX-ORG-010 | Organizational reparenting | Must | 002: Section 10 move confirmation; 033: FR-025, US5 |
| CX-ORG-011 | Independent identity | Must | 002: Section 10 move confirmation; 033: FR-026, US4 |
| CX-PER-001 | Preference profile | Must | 008: FR-021, US2 |
| CX-PER-002 | Admin-personalization policy | Must | 008: FR-022, US2 |
| CX-PER-003 | Add eligible placements | Must | 008: FR-023, US2 |
| CX-PER-004 | Remove and hide | Must | 008: FR-024, US2 |
| CX-PER-005 | Reorder and resize | Must | 008: FR-025, US2 |
| CX-PER-006 | Personal layout presets | Should | 008: FR-026 |
| CX-PER-007 | User reset and undo | Must | 008: FR-027, US2 |
| CX-PER-008 | Audience targeting | Must | 008: FR-028, US3 |
| CX-PER-009 | Rule-based visibility | Must | 008: FR-029, US3 |
| CX-PER-010 | Default fallback | Must | 008: FR-030, US3 |
| CX-PER-011 | Preference priority | Must | 008: FR-031, US3 |
| CX-PER-012 | Device-specific choices | Should | 008: FR-032 |
| CX-PER-013 | Privacy and consent | Must | 008: FR-033, US6 |
| CX-PER-014 | Preference portability | Should | 008: FR-034, US6 |
| CX-PER-015 | Safe smart defaults | Should | 008: FR-035, US6 |
| CX-EDT-001 | Visual builder | Must | 032: FR-036 |
| CX-EDT-002 | Component picker | Must | 032: FR-037, US1 |
| CX-EDT-003 | Placement operations | Must | 032: FR-038, US2 |
| CX-EDT-004 | Selection and properties | Must | 032: FR-025, US1 |
| CX-EDT-005 | Keyboard composition | Must | 032: FR-039, US2 |
| CX-EDT-006 | Live responsive preview | Must | 032: FR-040 |
| CX-EDT-007 | Persona and scope preview | Must | 032: FR-041 |
| CX-EDT-008 | Autosave drafts | Must | 032: FR-042, US6 |
| CX-EDT-009 | Undo redo and history | Must | 032: FR-043, US2 |
| CX-EDT-010 | Page governance | Must | 032: FR-050, US6 |
| CX-EDT-011 | Collision validation | Must | 032: FR-010, US1, US3 |
| CX-EDT-012 | Content vs presentation | Must | 032: FR-027, US1 |
| CX-EDT-013 | Global edits | Must | 032: FR-044, US6 |
| CX-EDT-014 | Reusable configuration fragments | Should | 032: FR-045 |
| CX-EDT-015 | Publishing audit | Must | 032: FR-055 |
| CX-NAV-001 | Multi-level navigation | Must | 008: FR-011, US4 |
| CX-NAV-002 | Accessible menu structures | Must | 008: FR-012, US4 |
| CX-NAV-003 | Information architecture | Must | 008: FR-013, US4 |
| CX-NAV-004 | Breadcrumbs | Must | 008: FR-014, US4 |
| CX-NAV-005 | Cross-scope navigation | Must | 008: FR-015, US4 |
| CX-NAV-006 | Search and quick navigation | Must | 008: FR-016, US4 |
| CX-NAV-007 | Favorites and recents | Should | 008: FR-017, US4 |
| CX-NAV-008 | Navigation adaptation | Must | 008: FR-018, US4 |
| CX-NAV-009 | Empty and dead ends | Must | 008: FR-019, US1 |
| CX-NAV-010 | In-context assistance | Should | 008: FR-020 |
| CX-INT-001 | Primary action clarity | Must | 008: FR-037, US5 |
| CX-INT-002 | Input validation | Must | 008: FR-038, US5 |
| CX-INT-003 | Feedback timing | Must | 008: FR-039, US5 |
| CX-INT-004 | Safe destructive actions | Must | 008: FR-040, US5 |
| CX-INT-005 | Interaction predictability | Must | 008: FR-041, US5 |
| CX-INT-006 | Clarity of copy | Must | 008: FR-042 |
| CX-INT-007 | Density control | Must | 008: FR-043, US5 |
| CX-INT-008 | Meaningful motion | Must | 008: FR-044, US5 |
| CX-INT-009 | Skeleton and loading states | Must | 008: FR-045, US5 |
| CX-INT-010 | User assistance | Should | 008: FR-046 |
| CX-INT-011 | Touch friendliness | Should | 008: FR-047 |
| CX-INT-012 | Selection and bulk actions | Should | 008: FR-048, US5 |
| CX-ACC-001 | Conformance baseline | Must | 007: FR-018, US5 |
| CX-ACC-002 | Keyboard access | Must | 007: FR-019, US5 |
| CX-ACC-003 | Drag alternatives | Must | 007: FR-019, US5 |
| CX-ACC-004 | Focus management | Must | 007: FR-020, US5 |
| CX-ACC-005 | Contrast compliance | Must | 007: FR-021, US5 |
| CX-ACC-006 | Text scaling | Must | 007: FR-022, US5 |
| CX-ACC-007 | Reflow | Must | 007: FR-022, US5 |
| CX-ACC-008 | Screen reader semantics | Must | 007: FR-023, US5 |
| CX-ACC-009 | Non-color indicators | Must | 007: FR-024, US5 |
| CX-ACC-010 | Reduced motion | Must | 007: FR-025, US5 |
| CX-ACC-011 | Alternative text support | Must | 007: FR-026, US5 |
| CX-ACC-012 | Accessibility in preview | Must | 007: FR-027 |
| CX-ACC-013 | Accessible personalization | Must | 007: FR-028, US5 |
| CX-ACC-014 | Error recovery | Must | 007: FR-029, US5 |
| CX-LOC-001 | Language catalog | Must | 007: FR-001, FR-012 |
| CX-LOC-002 | RTL switching | Must | 007: FR-002, US1 |
| CX-LOC-003 | Language overrides | Must | 007: FR-011, US1 |
| CX-LOC-004 | Mixed-script content | Must | 007: FR-013, US2 |
| CX-LOC-005 | Locale formatting | Must | 007: FR-014, US4 |
| CX-LOC-006 | Localized branding | Should | 033: FR-028, US3 |
| CX-LOC-007 | Translation completeness | Should | 007: FR-017 |
| CX-LOC-008 | Text expansion resilience | Must | 007: FR-015 |
| CX-LOC-009 | Font fallback | Must | 007: FR-010 |
| CX-LOC-010 | Direction-neutral layouts | Must | 007: FR-007, FR-016 |
| CX-EXT-001 | Component registration | Must | 032: FR-024, US7 |
| CX-EXT-002 | No hardcoded component IDs | Must | 032: FR-014 |
| CX-EXT-003 | Capability descriptors | Must | 032: FR-024, US1 |
| CX-EXT-004 | Schema-driven configuration | Must | 032: FR-025, US1 |
| CX-EXT-005 | Theme contract | Must | 032: FR-026 |
| CX-EXT-006 | Data boundary | Must | 032: FR-027, US7 |
| CX-EXT-007 | Context contract | Must | 032: FR-028, US7 |
| CX-EXT-008 | Error isolation | Must | 032: FR-029, US4 |
| CX-EXT-009 | Version compatibility | Must | 032: FR-030, US7 |
| CX-EXT-010 | Runtime security | Must | 032: FR-031 |
| CX-EXT-011 | Events and telemetry | Should | 032: FR-032 |
| CX-EXT-012 | Independent deployment | Should | 032: FR-033, US7 |
| CX-EXT-013 | Placement state contract | Must | 032: FR-034 |
| CX-EXT-014 | No arbitrary HTML execution | Must | 032: FR-035, US8 |
| CX-GOV-001 | Role permissions | Must | 004: FR-011 (with spec 032 FR-048); 032: FR-048 |
| CX-GOV-002 | Scoped ownership | Must | 032: FR-049 |
| CX-GOV-003 | Draft preview publish | Must | 032: FR-050, US1 |
| CX-GOV-004 | Approval workflow | Must | 032: FR-051, US6 |
| CX-GOV-005 | Version history | Must | 032: FR-052, US6 |
| CX-GOV-006 | Schedule releases | Should | 032: FR-053, US6 |
| CX-GOV-007 | Impact reporting | Must | 032: FR-054, US6 |
| CX-GOV-008 | Change conflict resolution | Must | 032: FR-042, US6 |
| CX-GOV-009 | Audit records | Must | 032: FR-055 |
| CX-GOV-010 | Safe rollback | Must | 032: FR-052, US6 |
| CX-GOV-011 | Delegated publishing | Must | 032: FR-056, US6 |
| CX-GOV-012 | Feature controls | Should | 032: FR-057 |
| CX-MET-001 | Experience telemetry | Must | 021: FR-017, US9 |
| CX-MET-002 | Privacy aware analytics | Must | 021: FR-018, US9 |
| CX-MET-003 | Template adoption | Should | 021: FR-019, US9 |
| CX-MET-004 | Personalization uptake | Should | 021: FR-019, US9 |
| CX-MET-005 | Performance per scope | Must | 021: FR-020, US9 |
| CX-MET-006 | Front-end error observability | Must | 021: FR-020, US9 |
| CX-MET-007 | Usability outcomes | Should | 021: FR-021, US9 |
| CX-MET-008 | Experiment governance | Could | 021: FR-022, US9 |
| CX-NFR-001 | Core Web Vitals | Must | 032: FR-058 |
| CX-NFR-002 | Lazy execution | Must | 032: FR-059 |
| CX-NFR-003 | Asset optimization | Must | 032: FR-060 |
| CX-NFR-004 | Layout stability | Must | 032: FR-061 |
| CX-NFR-005 | Isolation | Must | 032: FR-029, US4 |
| CX-NFR-006 | Offline state | Should | 032: FR-062 |
| CX-NFR-007 | Browser coverage | Must | 032: FR-063 |
| CX-NFR-008 | Device coverage | Must | 032: FR-063, US3 |
| CX-NFR-009 | Runtime monitoring | Must | 032: FR-064, US4 |
| CX-NFR-010 | Security hardening | Must | 026: FR-025 (with spec 032 FR-035); 032: FR-035, US8 |
| CX-NFR-011 | Cache correctness | Must | 032: FR-065, US8 |
| CX-NFR-012 | Failure recovery | Must | 032: FR-066, US4 |

## Source sections outside the numbered catalogue

| Source section | Covered by |
|---|---|
| 1.3 Design constraints | 032 section 6 and FR-014, FR-035; 008 section 6; 033 FR-013, FR-019 |
| 2.1 to 2.3 Experience model, independence and precedence | 032 FR-001 to FR-003; 033 FR-004, FR-017; 008 FR-031; ADR-0012 |
| 3 Roles and permissions | 032 section 3 and FR-048; 033 section 3; 008 section 3; 004 FR-011 |
| 4.1 Twelve templates, 4.2 ten layouts, 4.3 visual variants | 032 FR-006, FR-015, FR-016 |
| 6.1 to 6.4 Canonical journeys | 033 US1, US2, US6; 032 US1; 008 US2 |
| 7.1 Page definition, 7.2 manifest, 7.3 preference record | 032 FR-001, FR-024; 008 FR-036 |
| 8.1 Scenarios S01 to S12 | S01 033 SC-001; S02 032 SC-002; S03 008 SC-002; S04 008 SC-003 and 032 US5; S05 007 US1; S06 032 US3; S07 032 SC-003; S08 033 SC-002; S09 007 SC-004 and 032 SC-004; S10 032 SC-005; S11 032 US8; S12 033 SC-003 |
| 8.2 Acceptance test dimensions | 007 SC-005 |
| 8.3 Delivery waves | Roadmap R1 (wave 1 and home personalization), R2 (waves 2 to 4) |
| Appendix architecture rules | ADR-0012; 032 FR-001, FR-002, FR-025, FR-034; 033 FR-013 |
