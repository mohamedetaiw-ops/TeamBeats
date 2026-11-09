# Tasks: Document Management and Knowledge Base

Depends on 004 (resource grants), 005, 006, 007, 009 (acknowledgement, body document, knowledge content type), 016 (scanner port, inbox), 024 (engine core), 032 (component manifests). R1 covers User Stories 1 to 4 with TeamBeats libraries only (FR-023).

## Phase 1: Foundations
- [x] T001 `Documents` and `Documents.Contracts` projects; migration for libraries, folders, types, documents, versions and knowledge feedback (forced RLS)
- [x] T002 Permissions `doc.contribute`, `doc.manage`, `kb.edit`; audit actions of section 13 for R1; capability keys `docs.core`, `docs.controlled`, `docs.storage_gb`
- [x] T003 Object paths, signed download tokens, upload streaming with hash and allowlist checks

## Phase 2: User Story 1, store and find (P1)
- [x] T004 [P] [US1] Tests: required fields keep a checked-out draft; type and size refusal; quarantine on scan hit; sibling reader denied and hidden; metadata filters and saved views; cross-tenant
- [x] T005 [US1] Libraries, folders, types, upload, metadata and query endpoints; permission breaks

## Phase 3: User Story 2, check-out and versions (P1)
- [x] T006 [P] [US2] Tests: exclusive check-out with holder shown; minor versions hidden from readers; forced check-in keeps a draft and is audited; compare for text formats; restore creates a version; recycle bin delete and restore
- [x] T007 [US2] Check-out, check-in, versions, compare, restore and recycle bin

## Phase 4: Preview
- [x] T008 Preview for PDF, image, video and text; `IDocumentConverter` port with the Gotenberg adapter for Office formats and a fake for tests

## Phase 5: User Story 3, controlled documents (P1)
- [x] T009 [P] [US3] Tests: route on submit with an inbox task; major version with effective date and notice; superseded banner; read confirmation on effective; review task; PDF footer; no self-approval; refused without `docs.controlled`
- [x] T010 [US3] Numbering, route, effective scheduler, superseded state, review tasks, read confirmation and footer

## Phase 6: User Story 4, knowledge base (P1)
- [ ] T011 [P] [US4] Tests: SOP template sections and lifecycle; helpfulness score; FAQ pairs as separate index units with citation anchors
- [ ] T012 [US4] Knowledge articles on the spec 009 content contract; feedback

## Phase 7: Experience
- [ ] T013 Library browser with filters and saved views, document page with versions and preview, upload dialog, knowledge base pages, documents component for spec 032, in English and Arabic, RTL; e2e with axe

## Final phase
- [ ] T014 OpenAPI; R1 implementation notes (scanner, converter, controlled routes); spec evidence
