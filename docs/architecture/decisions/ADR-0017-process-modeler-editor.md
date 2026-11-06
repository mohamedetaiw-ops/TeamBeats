# ADR-0017: Process modeler built on MIT licensed libraries

| Field | Value |
|---|---|
| Status | Accepted (2026-11-05 by Mohamed) |
| Date | 2026-11-05 |
| Owners | Mohamed |
| Related specs | 024 (modeler, FR-002, sections 10 and 12, open question 1), 016 (simple designer); constitution XXII, XXIII, XXIX, XXX; ADR-0010, ADR-0014 |

## Context and constraints
ADR-0010 chose our own engine for a BPMN 2.0 subset with a browser modeler "built on an open source BPMN editor, subject to the licence scan". The usual choice, bpmn-js and dmn-js from bpmn.io, ships under the bpmn.io licence: MIT terms plus a condition that the bpmn.io watermark stays visible. That licence is not OSI approved, so constitution XXII would need an exception ADR, and the watermark cannot be removed from a white-labelled product (spec 033). Spec 024 also needs things the stock editor does not give us for free: only the supported subset in the palette, properties panels in Arabic and English, mirrored panels in RTL, full keyboard operation and a text outline for screen readers.

## Decision drivers
Only OSI licensed dependencies (XXII); no third-party branding in a white-labelled UI; React, because the frontend is React (ADR-0014); keyboard and screen reader support (XXIII, spec 024 section 10); standard BPMN 2.0 and DMN 1.3 XML in and out so models stay portable (024 US import, R3).

## Options considered
1. **bpmn-js and dmn-js.** Most complete BPMN editor. bpmn.io licence is not OSI approved and requires the watermark. Rejected.
2. **diagram-js (MIT) with our own BPMN rendering and rules.** diagram-js is the MIT core under bpmn-js, but it is not React, and the BPMN rendering, modelling rules and palette that bpmn-js adds would all have to be rewritten anyway.
3. **Our own modeler on React Flow (`@xyflow/react`, MIT) with `bpmn-moddle` and `dmn-moddle` (both MIT) for XML (chosen).** React Flow gives the canvas, nodes, edges, selection, zoom, keyboard handling and accessible labels. The moddle libraries read and write standard BPMN 2.0 and DMN XML, including vendor extensions. `@dagrejs/dagre` (MIT) lays out imported diagrams that have no diagram interchange data. The DMN decision table editor is a plain React table component from our design system.
4. **Read-only view with `bpmn-visualization` (Apache 2.0) and editing as forms.** Licence is fine, but users could not draw processes, which spec 024 US1 requires. Kept as a fallback viewer if needed.

## Decision
Option 3.

1. Libraries: `@xyflow/react`, `bpmn-moddle`, `dmn-moddle`, `@dagrejs/dagre`, all MIT. Versions are pinned and pass the CI licence gate before use. No bpmn.io licensed package (bpmn-js, dmn-js, bpmn-js-properties-panel) may be added.
2. The palette is generated from the supported subset in spec 024 section 12, so an unsupported element cannot be drawn (024 FR-002).
3. Element shapes follow the BPMN 2.0 notation and are our own React components using design tokens, so the modeler follows the tenant theme (spec 033) with no third-party marks.
4. The model is saved as BPMN 2.0 XML with TeamBeats extension elements, validated on the server against the subset schema (constitution XXIX). The client never decides whether a model is valid for publishing.
5. The modeler is a micro frontend component under ADR-0014 rules: no business rules in React, events only through declared contracts.

## Consequences and risks
- More frontend work than adopting bpmn-js: shapes, connection rules and the properties panels are ours. Spec 024 needs custom panels in any case, and the subset is small.
- BPMN diagram interchange (positions and waypoints) must round-trip; an import and export test suite with sample files from other tools covers it.
- If React Flow ever changes licence, the canvas sits behind our own component boundary and can be replaced without touching saved models, which are plain BPMN XML.

## Security and multi-tenancy implications
Imported XML is parsed with external entities disabled and size limits, on the server as well as in the browser. Models, drafts and published versions are tenant-owned under RLS (ADR-0010). Extension elements are typed data, never script.
