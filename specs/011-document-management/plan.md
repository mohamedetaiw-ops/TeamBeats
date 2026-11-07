# Implementation Plan: Document Management and Knowledge Base

**Spec:** `specs/011-document-management/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0013, ADR-0016 (document source port), spec 024 plan (engine core), spec 009 plan (acknowledgement, body document), module map (`Documents`)

## Summary
R1 builds TeamBeats libraries with no external connection (FR-023): libraries, folders, document types with metadata, upload checks and malware scan, versions with check-out and check-in, compare for text formats, restore, preview, permissions with explicit breaks, the recycle bin, controlled documents with approval, effective dates, superseded state, review tasks and read confirmation (User Stories 1 to 3), and knowledge base articles (User Story 4). Office editing in the browser, the asset portal, retention, legal hold, watermarking, guest sharing, bulk import, OCR and SharePoint connected libraries are R2.

R1 slices, in order:
1. **Libraries and files:** libraries, folders, document types, upload with checks, scan, storage, download, permissions.
2. **Versions:** check-out, check-in, minor and major versions, forced check-in, compare, restore, recycle bin.
3. **Preview:** PDF, image, video and text in the browser; Office formats through a conversion service.
4. **Controlled documents:** numbering, owner, route on the engine core, effective date, superseded, review tasks, read confirmation, PDF footer.
5. **Knowledge base:** articles with templates, the spec 009 lifecycle, helpfulness, FAQ pairs as separate index units.
6. **Pages:** library browser with metadata filters and saved views, document page, upload, version history, knowledge base, the documents component for spec 032, in English and Arabic, RTL.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation and org scope | Yes | All tables tenant-owned with forced RLS; objects under `tenants/{tenant_id}/documents/{library_id}/`; libraries at tenant, org unit or site scope |
| III authorization | Yes | `doc.contribute`, `doc.manage`, `kb.edit` and resource permissions through `IAuthorizationService` with explicit inheritance breaks (spec 004 resource grants); readers without `doc.contribute` see only effective major versions (FR-006) |
| VII retrieval safety | Yes | `DocumentVersionPublished` and permission events let search (planned spec 012) keep permissions; nothing is sent to AI from this module |
| XIV modules | Yes | `Documents` owns libraries and files; approval on `Workflow`; acknowledgement through the `Content` contract of spec 009 |
| XXI entitlements | Yes | `docs.core` for all; `docs.controlled` gates controlled types; `docs.storage_gb` as a limit checked on upload |

## Data model (schema `documents`)
- `library`: names per locale, `scope` (tenant, org unit, site), `scope_id`, `allowed_types`, `max_file_mb`, `classification_default`, `version`.
- `folder`: tree inside a library, `inherits bool` (permission break flag).
- `document_type`: per library, `fields jsonb` (typed fields, required, defaults, term sets), `controlled jsonb` (numbering scheme, route, review months, read confirmation).
- `document`: `library_id`, `folder_id`, `type_id`, `number`, `metadata jsonb`, `classification`, `owner_user_id`, `checked_out_by`, `checked_out_at`, `current_major`, `current_minor`, `effective_version`, `review_due_at`, `deleted_at` (recycle bin), `version`.
- `document_version`: `document_id`, `major`, `minor`, `object_key`, `content_hash`, `size`, `content_type`, `note`, `author_user_id`, `approver_user_id`, `effective_from`, `state` (draft, in_review, approved, effective, superseded), `scan_state`, `text_extract` (for compare and search).
- `kb_article`: articles stored as `Content` items of type `knowledge` through the spec 009 contract, with template sections; `kb_feedback` (helpful, comment).

## Files
- Upload is streamed to object storage, hashed (SHA-256) for dedupe within a library (FR-003), checked against the allowlist and size limit, and scanned through the `IMalwareScanner` port shared with spec 016 (ClamAV adapter, listed for approval). Until clean, no preview or download; infected files are quarantined and the uploader and library manager notified.
- Download through a short-lived signed token (`file access tokens` in the module map), checked against permissions at issue time, audited `doc.downloaded` and published as `DocumentDownloaded` for the spec 026 volume alert.

## Preview (FR-009)
PDF, images, video and text render in the browser from the file itself. Office formats need conversion to PDF; the plan uses Gotenberg (MIT, LibreOffice inside, a separate container) behind an `IDocumentConverter` port, with the PDF cached in object storage per version. This is new infrastructure and is listed for approval; without it Office files show "preview not available, download to open" in R1.

## Controlled documents (FR-007, FR-008)
- Submit for release starts the document type's route on the engine core: one or more approver steps in sequence (US3: plant manager, then Quality). Controlled types need `docs.controlled`, which is the package gate for routes on documents.
- On approval the version becomes the next major with an effective date; a scheduler makes it effective on the date and marks the old one superseded and read-only.
- Read confirmation uses the spec 009 acknowledgement contract with the document's audience.
- PDF downloads of controlled documents get a footer stamped at conversion time: number, version, effective date and "uncontrolled when printed" (localized).

## API (v1)
- Libraries, folders and types: `GET/POST /api/v1/documents/libraries`, `.../{id}/folders`, `.../{id}/types`, permission break endpoints.
- Documents: `POST /api/v1/documents/libraries/{id}/files` (upload), `GET /api/v1/documents/{id}`, `PUT .../metadata`, `POST .../checkout`, `.../checkin`, `.../discard`, `.../force-checkin {reason}`, `GET .../versions`, `GET .../compare?from=&to=`, `POST .../restore`, `POST .../submit`, `GET .../download-token`, `GET .../preview`, `DELETE /api/v1/documents/{id}`, recycle bin list and restore.
- Query: `GET /api/v1/documents/libraries/{id}/items?filter=&sort=&view=` with metadata filters, saved views for library managers.
- Knowledge base: `GET/POST /api/v1/kb/articles`, feedback endpoint.

## Events
`DocumentVersionPublished`, `DocumentDeleted`, `DocumentDownloaded`, `DocumentPermissionsChanged` (IDs and facts only) for search, analytics and security.

## Tests
- Integration per user story: US1 scenarios 1 to 5, US2 scenarios 1 to 5, US3 scenarios 1 to 7, US4 scenarios 1 to 3 (4 with the assistant).
- Boundaries: library, document, version and token of another tenant not found; sibling unit reader denied without a grant; a broken inheritance folder hides its documents from parent readers; a download token cannot be reused by another user or tenant.
- Entitlements: controlled types refused without `docs.controlled`; storage limit error shape.
- Files: allowlist and size refusal, dedupe, quarantine, no preview before clean.
- Acceptance (e2e): create a library and type, upload, filter, check out and in, release a controlled version and confirm reading, write a knowledge article, in English and Arabic with axe.
