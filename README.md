# TeamBeats

Spec-driven development repository for TeamBeats (working name), an AI-native enterprise intranet. Built from `AI_Intranet_Product_Foundation` v0.1.

The layout follows GitHub spec-kit conventions so `/specify`, `/clarify`, `/plan` and `/tasks` style workflows work.

| Path | What it is |
|---|---|
| `docs/review/foundation-v0.1-review.md` | Critical review of the foundation, with the decisions needed |
| `docs/review/architecture-patterns-review.md` | Architecture patterns recommendation reasoned against the constitution |
| `.specify/memory/constitution.md` | The constitution (v1.1.0, ratified) |
| `.specify/templates/` | Spec, plan, tasks and ADR templates |
| `specs/` | Feature specs: 001 to 007 (Approved, with plans and tasks) and 008, 009, 011, 013, 016, 020, 021, 022, 024 to 027, 032, 033 (Proposed). Index, R0 build order and planned specs in `specs/README.md` |
| `docs/architecture/r0-solution-structure.md` | Shared R0 repo layout, building blocks, test harness and CI gates |
| `docs/architecture/module-map.md` | Backend modules as bounded contexts: owned data, contracts, events |
| `docs/architecture/decisions/` | ADR-0001 to ADR-0014 and ADR-0016. ADR-0005, ADR-0013 and ADR-0014 are Accepted; the rest are Proposed |
| `docs/product/roadmap.md` | Release slices R0 to R3 (adopted) |
| `docs/product/competitor-feature-coverage.md` | Where each competitor capability lives in the specs |
| `docs/product/composable-experience-requirements.md` | Composable Intranet Experience requirements (mandatory, 182 IDs cited as `CX-`) |
| `docs/product/composable-experience-coverage.md` | Spec and FR for each composable experience requirement |
| `specs/open-questions.md` | Remaining spec questions with defaults |
| `docs/product/foundation-v0.1.md` | The source foundation document, with its diagrams in `assets/` |
| `CLAUDE.md` | Rules for AI coding agents working in the repo |

## Status (2026-11-05)
- Review decisions 1 to 4: taken by Mohamed.
- Specs 001 to 007: Approved, each with `plan.md` and `tasks.md`. The Active Directory parts of 002 and 003 are pending approval; their plans and tasks are written.
- Specs 008, 009, 011, 013, 016, 020, 021, 022, 024, 025, 026, 027, 032, 033: Proposed. Spec 007 carries composable experience additions that need approval. Plans and tasks follow once each is approved.
- Constitution: v1.1.0, ratified by Mohamed (1.0.0 on 2026-11-04, 1.1.0 on 2026-11-05).
- ADR-0005, ADR-0013 and ADR-0014 Accepted. ADR-0001 to ADR-0004, ADR-0006 to ADR-0012 and ADR-0016 Proposed. ADR-0009 (data access) is also carried by the R0 build pull request.
- Next: approve the open ADRs and Proposed specs, then build R1 in the release order of `docs/product/roadmap.md`.
