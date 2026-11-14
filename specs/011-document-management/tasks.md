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
- [x] T011 [P] [US4] Tests: SOP template sections and lifecycle; helpfulness score; FAQ pairs as separate index units with citation anchors
- [x] T012 [US4] Knowledge articles on the spec 009 content contract; feedback

## Phase 7: Experience
- [x] T013 Library browser with filters and saved views, document page with versions and preview, upload dialog, knowledge base pages, documents component for spec 032, in English and Arabic, RTL; e2e with axe

## Final phase
- [x] T014 OpenAPI; R1 implementation notes (scanner, converter, controlled routes); spec evidence

## R2 Phase 8: User Stories 6 and 7, retention, disposition and legal hold (P2)
- [x] T015 Records Manager role and `records.manage`; retention labels by library, type and document; label in period refuses deletion; shorter period only on reapply
- [x] T016 Disposition: review queue, automatic deletion, archive, content-free disposition record, search told with `DocumentDisposed`
- [x] T017 Legal holds by custodian and range, library or folder, and document; every deletion path blocked and audited; release sends ended periods to review; audit purge keeps held records through the contract
- [x] T018 Records page (labels, where labels apply, disposition, holds) in English and Arabic, RTL; integration tests for each deletion path and cross-tenant targets; e2e with axe

## R2 Phase 9: User Story 8, view-only and guest sharing (P2)
- [x] T019 View-only libraries with a dynamic watermark and no download, enforced in the API
- [x] T020 Sharing folders with guest spaces with expiry, read-only, never for Restricted; every guest view audited

## R2 Phase 10: User Story 5, brand asset portal (P2)
- [x] T021 Collections, usage rights with expiry, renditions, access requests to the brand manager's inbox, download tracking

## R2 Phase 11: bulk import, OCR and Office editing
- [x] T022 Bulk import with a metadata manifest, original dates and authors, per-row report (FR-022)
- [x] T023 Office web editing through the Microsoft 365 connection (FR-010)
- [x] T026 Text from scanned PDFs and images by OCR for search where the tenant enables it (`docs.ocr`, FR-020)

## R2 Phase 12: User Stories 9 and 10, SharePoint Online
- [x] T024 Connected libraries through the Microsoft 365 connection with live permission checks, sync, degraded mode and label mapping
- [x] T025 Import from SharePoint into TeamBeats libraries through bulk import
