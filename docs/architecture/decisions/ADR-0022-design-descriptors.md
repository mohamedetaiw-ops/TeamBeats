# ADR-0022: Design descriptors, presentation variants as choice tokens

| Field | Value |
|---|---|
| Status | Accepted (2026-11-14, under Mohamed's standing approval to proceed) |
| Date | 2026-11-14 |
| Owners | Mohamed |
| Related specs | 036 (DLS-007, DLS-110), 033 (FR-030 to FR-033), 032 (FR-071 to FR-073), 008; constitution XXIX, XXX, XXXI; extends ADR-0021 |

## Context and constraints
ADR-0021 made a design a token set plus one structural choice, the navigation style. The UX, DLS and frontend review of 14 November 2026 found that the four designs therefore read as colour sets: the same page composition in another palette. Customers expect a chosen design to feel different (image led and editorial, or dense and operational) while the product keeps one component, permission and data architecture. Constitution XXIX forbids per-customer code and free CSS, and XXX keeps business rules out of the frontend.

## Options considered
1. **A component set per design.** Real difference, but four times the components to build and test, and behavior drifts between them. Rejected, as in ADR-0021.
2. **More structural CSS per design in `designs.css`.** Cheap, but the difference stays skin deep, the file grows without a contract, and a tenant cannot adjust it. Rejected.
3. **A descriptor of typed choice tokens per design (chosen).** A fixed list of shared parts each offer a small set of named presentation variants. A design names the variant it uses for each, plus its default density and imagery approach. The values are choice tokens resolved by the same resolver as `design.preset`.

## Decision
Option 3.
- New choice tokens: `density.default` (`comfortable`, `compact`), `imagery.style` (`image_led`, `balanced`, `typographic`), `variant.news_lead` (`editorial`, `compact`, `typographic`), `variant.stat` (`tinted`, `outlined`, `plain`), `variant.work_row` (`comfortable`, `dense`), `variant.tabs` (`underline`, `segmented`), `variant.card` (`raised`, `outlined`, `flat`), `variant.briefing` (`night`, `light`).
- `DesignPresets.cs` sets them per design; a tenant or organization brand may override a value with another from the list. They are validated, inherited, lockable, versioned and audited like any token.
- The web app exposes the resolved values as `data-variant-*` attributes on `<html>`, beside `data-design` and `data-nav`. A shared part reads its variant from the attribute and styles it in its own style sheet; a variant is part of the component, tested in the gallery, not a per-design rule.
- A variant changes presentation only. Every variant of a part takes the same props, renders the same data, offers the same actions in the same order and keeps the same accessible names. The component tests assert this for each part.
- Brand identity, design, layout preset, appearance mode and density stay separate values that never reset each other. A person's own density choice wins over `density.default` where the personalization policy allows it.
- Adding a part to the variant list, or a value to a part, is a change to spec 036 and this ADR.

## Consequences
- Choosing a design changes how the home's news lead, stat tiles, work rows, tabs, cards and briefing are presented, so designs differ in experience, not only in colour.
- The existing per-design rules in `designs.css` for those parts (for example Studio's light briefing and segmented tabs) move into variants and the file shrinks.
- The gallery and the design suite must show each variant of each part; the test matrix grows by the variants, not by designs times variants, because a variant is tested once.
- Tenants gain a safe way to mix (Atlas colours with editorial news) without CSS.
- Layout (which components sit where) is still owned by spec 032 templates and home views, not by the design.
