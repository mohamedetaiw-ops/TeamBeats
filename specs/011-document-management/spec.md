# Feature Specification: Document Management and Knowledge Base

| Field | Value |
|---|---|
| Spec ID | 011-document-management |
| Area | knowledge |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (libraries, metadata, versions, check-out and check-in, preview, permissions, knowledge base, controlled documents with approval), R2 (retention and disposition, legal hold, brand asset portal, watermarking and download controls, external sharing to guests, bulk import), R3 (records declaration with disposition certificates, customer-managed keys per library) |
| Last updated | 2026-11-05 |
| Requirement IDs | KNO-001, KNO-003, KNO-006, ADM-003, DOC-001 to DOC-022 (new) |
| Use cases | UC-04, UC-06 (as a source for the assistant), UC-14 (deletion and legal hold) |
| Depends on | 002, 004, 005, 006, 007, 009 (pages that link documents), 012 (search), 013 and 014 (AI), 016 (task inbox), 018 (SharePoint as an alternative store), 024 (process engine for approval and review routes), 025 (policies are controlled documents), 026 (external users, encryption, keys) |

## 1. Problem and outcome

**Problem:** Regulated organizations run on controlled documents: standard operating procedures, forms, manuals, quality records, brand files. They are kept today on file shares and in email, with no single current version, no check-out, no approval trail and no review cycle. Auditors ask "show me the version in force on 3 March, who approved it and who could read it", and nobody can answer quickly. Many customers also keep documents in SharePoint and do not want to move them.

**Outcome:** TeamBeats offers document libraries with metadata, major and minor versions, check-out and check-in, approval before publishing, review dates, retention and legal hold. Readers always see the current approved version, and editors can see and restore every earlier one. A knowledge base holds versioned articles such as SOPs, walkthroughs and FAQs. A brand asset portal gives approved logos, templates and images in the right formats. Everything is searchable and usable by the AI assistant, with the reader's permissions. Customers who keep files in SharePoint get the same reading experience through spec 018.

## 2. Scope

**In scope:**
- Libraries per tenant, per site (spec 009) and per org unit, with folders, and permissions set at library, folder or document level by inheritance with explicit breaks.
- Document types (content types) with metadata fields, required fields, managed term sets (taxonomy), and default values per library.
- Upload (single, multiple, drag and drop, bulk import with metadata file), file type allowlist, size limits, malware scanning, duplicate detection by hash.
- Versioning: minor versions for drafts and major versions for approved releases, version notes, compare for text formats, restore.
- Check-out and check-in: exclusive editing lock, check-out by self, discard check-out, forced check-in by library managers with a reason.
- Editing: download and upload, and editing in the browser for Office formats through Microsoft 365 when connected (spec 018). TeamBeats keeps its own version on check-in.
- Controlled documents: document number, owner, approval route before a major version is published (spec 024), effective date, review date, superseded versions kept read-only, "controlled copy" watermark, "uncontrolled when printed" footer.
- Preview in the browser for common formats (PDF, Office, images, video, text) without download.
- Knowledge base: articles with templates for SOP, how-to walkthrough, FAQ and troubleshooting, version-controlled, with owners, review dates, related articles and "was this helpful" feedback.
- Brand asset portal: curated asset collections with usage rights, expiry, renditions (sizes and formats), download tracking and request for access.
- Retention: retention labels with a period and an action (delete, review, archive), applied by library default, document type or manually; disposition review queue.
- Legal hold: hold by custodian, library, folder, query or individual items; held items cannot be deleted, purged or disposed.
- Download and sharing controls: block download, view-only preview, dynamic watermark with the viewer's name and time, share by link to permitted users only, external sharing to guests (spec 026) where the library allows it.
- Recycle bin with restore for a configurable period.
- Document subscriptions: follow a document or folder and get notified of new major versions.
- Read receipts for controlled documents, reusing acknowledgement records from spec 009 and attestation from spec 025.

**Out of scope:**
- Policy lifecycle, attestation campaigns and policy Q&A (spec 025). Policies are stored here as controlled documents or as pages from spec 009.
- Full co-authoring editor built into TeamBeats. Co-authoring is delegated to Microsoft 365 or Google Workspace when connected.
- Electronic signatures with legal standing. Approval in the route is a recorded decision, not a qualified e-signature. [NEEDS CLARIFICATION: is a qualified e-signature integration needed for government customers?] Default: integration through spec 017 in R3.
- Syncing a desktop folder (no sync client).

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Reader | View, preview and download (if allowed) current approved versions | CanView on item |
| Contributor | Upload, edit metadata, check out and check in, create minor versions | `doc.contribute` on library or folder |
| Approver | Approve a major version in their route step | Assigned by the route (spec 024) |
| Document owner | Answer for a controlled document, get review tasks, start a new revision | Named on the document |
| Library manager | Configure library, document types, permissions breaks, retention defaults, force check-in | `doc.manage` on library |
| Records manager | Manage retention labels, disposition reviews, legal holds | `records.manage` at tenant scope |
| Brand manager | Curate asset collections, usage rights, renditions | `assets.manage` on the portal |
| Knowledge editor | Write and publish knowledge articles | `kb.edit` on the knowledge base |
| Guest (spec 026) | View documents shared with their guest space, read-only | Guest share |

## 4. User scenarios

### User Story 1: Store and find documents with metadata (Priority: P1, R1)

The Quality team creates a "Quality procedures" library with a document type "SOP" that requires Process, Site and Document number fields. Contributors upload files and fill in the metadata. Readers filter by Process and Site.

**Independent test:** create a library and document type with required fields; upload; filter; check permissions from another unit.

**Acceptance scenarios:**
1. **Given** a library with a document type that has required fields, **When** a contributor uploads without them, **Then** the file stays as a checked-out draft visible only to them until the fields are filled.
2. **Given** a file whose type is not on the tenant allowlist or that exceeds the size limit, **Then** the upload is refused with a message.
3. **Given** a file flagged by the malware scanner, **Then** it is quarantined, never previewed or downloaded, and the uploader and library manager are notified.
4. **Given** a reader from a sibling unit without a permission grant, **When** they open the library URL, **Then** they get no access, and the library's documents do not appear in their search results.
5. **Given** metadata filters, **Then** readers can filter and sort by any field, and saved views can be created by library managers.

### User Story 2: Edit with check-out and versions (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a document, **When** contributor A checks it out, **Then** others can read the last version but cannot edit or upload a new version, and they see who has it checked out and since when.
2. **Given** A checks in with a version note, **Then** a minor version (for example 2.1) is created; readers still see the last major version (2.0) unless they have `doc.contribute`.
3. **Given** A is away and the document is needed, **When** the library manager forces check-in with a reason, **Then** A's unsaved changes are kept as a separate draft for A, and the action is audited.
4. **Given** two versions of a text-based document, **When** an editor compares them, **Then** additions and removals are shown.
5. **Given** an editor restores version 1.0, **Then** a new minor version is created from it. No version is ever deleted except by retention or recycle bin purge.
6. **Given** Microsoft 365 is connected, **When** a contributor chooses "Edit in browser", **Then** the document opens in the Office web editor, and on check-in TeamBeats stores the result as a new version with the contributor as author.

### User Story 3: Controlled document approval and review (Priority: P1, R1)

The Health and Safety manager revises the "Working at height" procedure. The new major version needs approval from the plant manager and Quality. Once approved it becomes effective on Monday, the previous version becomes superseded, and readers in the plants are asked to confirm they have read it.

**Acceptance scenarios:**
1. **Given** a controlled document type with a route, **When** the owner submits a minor version for release, **Then** the route runs on spec 024 and tasks appear in the task inbox (spec 016).
2. **Given** all approvals pass, **Then** the version becomes major (3.0) with an effective date. Before the effective date readers see 2.0 with a notice "new version effective on ..."; on the effective date 3.0 becomes current and 2.0 becomes superseded and read-only.
3. **Given** a reader opens a superseded version from an old link, **Then** a banner says it is superseded with a link to the current version.
4. **Given** the document type requires read confirmation, **When** a major version becomes effective, **Then** an acknowledgement request goes to the document's audience using the mechanism of spec 009 (FR-006, FR-007).
5. **Given** a review date, **When** the reminder window starts, **Then** the owner gets a review task to confirm, revise or withdraw. Overdue reviews show on the knowledge health dashboard.
6. **Given** a controlled document is printed or downloaded as PDF, **Then** a footer shows document number, version, effective date and "uncontrolled when printed".
7. **Given** an approver who is also the author, **Then** they cannot approve their own revision.

### User Story 4: Knowledge base articles (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a knowledge editor, **When** they create an article from the SOP template, **Then** the article has sections for purpose, scope, responsibilities, steps and related documents, and follows the same draft, review, publish and version lifecycle as content in spec 009.
2. **Given** a published article, **Then** readers can mark it helpful or not helpful with an optional comment, and the owner sees the score.
3. **Given** an article with the FAQ template, **Then** each question and answer pair is indexed separately for search and for the assistant (spec 014), with a citation link to the exact pair.
4. **Given** the assistant cannot answer a question from the knowledge base, **Then** the unanswered question is logged (without personal data beyond the user ID if the tenant allows) so knowledge editors can see gaps (spec 021).

### User Story 5: Brand asset portal (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a brand manager uploads a logo, **Then** the portal produces renditions (PNG at set sizes, SVG, CMYK PDF where the source allows) and shows usage notes.
2. **Given** an asset with usage rights that expire, **When** the expiry date passes, **Then** the asset is hidden from the portal and from the page builder picker, and pages that use it are listed to their owners.
3. **Given** an asset collection restricted to Marketing, **When** another employee requests access, **Then** a request goes to the brand manager's task inbox.
4. **Given** downloads, **Then** the portal records who downloaded which asset and rendition, for usage reporting.

### User Story 6: Retention and disposition (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a retention label "Quality record, 10 years after superseded, then review", **When** a document's trigger date plus the period passes, **Then** it enters the disposition review queue for the records manager.
2. **Given** the records manager approves disposition, **Then** the document and all versions are deleted, a disposition record (document number, label, date, approver, no content) is kept, and the deletion propagates to search and vector indexes within the KNO-006 objective.
3. **Given** a label with an automatic delete action, **Then** deletion happens without review and is audited with counts.
4. **Given** a user tries to delete a document with a retention label in its retention period, **Then** deletion is refused.
5. **Given** a label is changed to a shorter period on a library, **Then** the change applies only to documents labelled after the change unless the records manager confirms a reapply.

### User Story 7: Legal hold (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a records manager creates a hold for custodian "Omar" and a date range, **Then** every document Omar created or edited in that range, in every library, is held.
2. **Given** a held document, **When** anyone (including retention jobs, recycle bin purge, tenant closure jobs in spec 001 and tenant data deletion requests in UC-14) tries to delete it, **Then** deletion is refused and the attempt is audited.
3. **Given** the hold is released, **Then** normal retention resumes, and documents whose period has passed enter disposition review rather than being deleted at once.
4. **Given** a hold, **Then** holders and the hold reason are visible only to records managers.

### User Story 8: Restrict download and share with guests (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a library set to view-only, **When** a reader opens a document, **Then** they get a preview with a watermark showing their name, email and time, and no download, print or copy actions. The API refuses download requests.
2. **Given** a library that allows guest sharing and a guest space (spec 026), **When** a library manager shares a folder with an auditor guest for 30 days, **Then** the auditor can view (read-only) until the share expires, and every view is audited.
3. **Given** a library classified Restricted, **Then** guest sharing is not available.

### Edge cases and exception flows
- A user uploads a new version while someone else holds the check-out: refused with the holder's name.
- A check-out older than the library's limit (default 14 days): owner and library manager are reminded.
- A document moved between libraries with different permissions: permissions follow the destination, and the move is audited. Moving a held document is allowed, deleting is not.
- Preview conversion fails: the reader sees a "preview not available" message with download if allowed.
- A very large file (above 2 GB by default) is refused; video goes to the media pipeline with streaming renditions.
- Two documents with the same document number in one numbering scheme: refused.
- A controlled document's approver leaves the company mid-route: the route reassigns to the step's fallback (spec 024).
- Encrypted or password-protected files: stored, flagged as "not indexed", never sent to AI.

## 5. Functional requirements

- **FR-001 / DOC-001**: The system MUST provide document libraries at tenant, site and org unit scope, with folders and permission inheritance that can be broken explicitly at folder or document level. *(KNO-001)*
- **FR-002 / DOC-002**: Library managers MUST be able to define document types with typed metadata fields, required fields, defaults and managed term sets. *(KNO-003)*
- **FR-003 / DOC-003**: Uploads MUST be checked against a tenant file type allowlist and size limit, scanned for malware before preview or download, and deduplicated by content hash within a library.
- **FR-004 / DOC-004**: The system MUST keep minor and major versions with author, time, note and approver, MUST allow compare for text formats and restore as a new version, and MUST NOT delete versions except through retention, disposition or recycle bin purge.
- **FR-005 / DOC-005**: The system MUST support exclusive check-out with visible holder, check-in with note, discard, and forced check-in with reason by library managers.
- **FR-006 / DOC-006**: Readers without `doc.contribute` MUST see only the latest major version that is effective.
- **FR-007 / DOC-007**: Controlled document types MUST support document numbering schemes, owner, approval route on spec 024, effective date, superseded state, review date with review tasks, and print and PDF footer with control details. Authors MUST NOT approve their own revision.
- **FR-008 / DOC-008**: Controlled document types MUST be able to require read confirmation on each new major version, using the acknowledgement record of spec 009.
- **FR-009 / DOC-009**: The system MUST preview PDF, Office, image, video and text formats in the browser without download.
- **FR-010 / DOC-010**: When Microsoft 365 is connected (spec 018), contributors MUST be able to edit Office documents in the browser; TeamBeats MUST store the result as its own version on check-in.
- **FR-011 / DOC-011**: The knowledge base MUST support article templates (SOP, how-to, FAQ, troubleshooting), the lifecycle and versioning of spec 009, related articles, helpfulness feedback and per-question indexing for FAQ articles.
- **FR-012 / DOC-012**: The brand asset portal MUST support collections, usage rights with expiry, automatic renditions, access requests and download tracking.
- **FR-013 / DOC-013**: Records managers MUST be able to define retention labels with period, trigger (created, modified, effective, superseded, custom date field) and action (delete, review, archive), and apply them by library, document type or manually. *(ADM-003)*
- **FR-014 / DOC-014**: Disposition MUST keep a content-free disposition record and propagate deletion to search and vector indexes within the KNO-006 objective. *(KNO-006)*
- **FR-015 / DOC-015**: Legal holds by custodian, location, query or item MUST block every deletion path in the product, including retention, purge, user deletion and tenant closure, until released.
- **FR-016 / DOC-016**: Libraries MUST support view-only mode with dynamic watermark and no download, enforced in the API as well as the UI.
- **FR-017 / DOC-017**: Libraries MUST support sharing with guest spaces (spec 026) with expiry, read-only by default, never for Restricted classification.
- **FR-018 / DOC-018**: Deleted items MUST go to a recycle bin for 93 days by default (tenant configurable between 30 and 180), restorable by the deleter or library manager, unless a retention label forbids deletion.
- **FR-019 / DOC-019**: Every document MUST carry a classification (Public, Internal, Confidential, Restricted) that limits AI use, guest sharing and download policy. *(KNO-003)*
- **FR-020 / DOC-020**: All documents MUST be indexed for search (spec 012) with source, version, language, classification and permissions, including text extracted by OCR from scanned PDFs and images where the tenant enables it. *(KNO-003)*
- **FR-021 / DOC-021**: Users MUST be able to follow a document or folder and be notified of new major versions through spec 015.
- **FR-022 / DOC-022**: Bulk import MUST accept files with a metadata manifest, preserve original created and modified dates and authors where mapped, and report each row's result.

## 6. Business and validation rules
- Minor versions are kept up to 50 per major version by default; older minor versions are pruned when a major version is approved, unless the library keeps all.
- Check-out limit default 14 days with reminders; no automatic release.
- A document can have one retention label at a time. The longest applicable hold or retention wins.
- Effective date cannot be earlier than the approval date.
- Document numbers are unique per numbering scheme and never reused, even after disposition.
- A superseded controlled document stays readable by contributors and auditors, not by ordinary readers, unless the library shows history to readers.

## 7. Tenant and organization scope
- Tenant-owned data: libraries, documents, versions, metadata, labels, holds, assets, articles, check-out state, watermarks.
- Object storage paths carry the tenant ID (constitution I). Each tenant's files are encrypted with tenant-scoped keys (spec 026).
- Organization scoping: library scope plus item permissions.
- Cross-tenant denial cases: download by file ID from another tenant; preview token reuse across tenants; a legal hold query that tries to match another tenant's custodian; search returning another tenant's document.
- Sibling, parent and child boundary cases: a library at "Acme Holding" is not readable by "Acme Gulf" members unless granted; a library manager of a child unit cannot change a parent library; folder permission breaks do not widen access beyond the grant.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Document file and versions | Product (object storage) or SharePoint via 018 | Per document | Retention label, recycle bin | Possibly (content) |
| Document metadata | Product | Same as document | Same as document | Author IDs |
| Disposition record | Product | Internal | 10 years default | Approver ID |
| Legal hold | Product | Confidential | Until released plus 7 years | Custodian IDs |
| Knowledge article | Product | Per article | Content type setting | Author IDs |
| Asset and renditions | Product | Internal | Usage rights expiry | No |
| Download log | Product | Internal | 1 year default | Yes |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `docs.core` (libraries, metadata, versions, check-out, preview, knowledge base) | on | on | on |
| `docs.storage_gb` (per tenant) | [TBD] | [TBD] | [TBD] |
| `docs.controlled` (numbering, approval routes, effective dates, read confirmation) | off | on | on |
| `docs.asset_portal` | off | on | on |
| `records.retention` (labels, disposition) | off | on | on |
| `records.legal_hold` | off | off | on |
| `docs.view_only_watermark` | off | on | on |
| `docs.guest_sharing` | off | on | on |
| `docs.ocr` | off | on | on |

## 10. UX, localization and accessibility
- States: empty library, uploading, scanning, quarantined, checked out by me, checked out by someone else, draft, in approval, effective soon, current, superseded, held (visible only to records managers), preview unavailable, no permission.
- Metadata labels and term sets support locale variants; document titles and content are not translated automatically.
- Arabic file names and metadata sort and search correctly; preview supports right-to-left PDFs.
- Drag and drop always has a keyboard alternative ("Upload files" button); version compare uses text markers besides color.

## 11. AI behavior and data policy
- Documents feed search and the assistant (specs 012, 014) only with the reader's permissions at query time (constitution VII), with citations to document, version and page.
- AI use of a document follows its classification and the tenant AI policy. Restricted documents are never sent to external providers by default.
- AI can suggest metadata (document type, fields, tags) on upload; suggestions are shown for the contributor to accept, never applied silently.
- AI summaries of documents are generated on request, labelled, and not stored as the document's content.
- Text inside documents is untrusted input (constitution XI).

## 12. Integrations and events
- Publishes: `DocumentVersionPublished`, `DocumentSuperseded`, `DocumentDeleted`, `DocumentDisposed`, `LegalHoldApplied`, `LegalHoldReleased`, `DocumentReviewDue`.
- Consumes: `UserDeprovisioned` (owner reassignment and check-out release to library manager), approval results from spec 024.
- SharePoint and OneDrive (spec 018): read and index SharePoint libraries in place, with SharePoint permissions mirrored for search; this spec's controlled document features apply only to TeamBeats libraries.
- Google Drive and Docs (INT-004): same pattern as SharePoint, later release.
- Office web editing through Microsoft 365 (WOPI or Graph), through the connector framework (spec 017).

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| doc.uploaded / version_created / restored / moved / deleted / restored_from_bin | audit | document, version, library, actor |
| doc.checked_out / checked_in / forced_check_in | audit | document, actor, reason |
| doc.approved / released / superseded | audit | document, version, route, approvers |
| doc.permissions_broken / changed | audit | target, before, after, actor |
| doc.shared_with_guest / guest_viewed | audit | document or folder, guest space, expiry |
| doc.downloaded | audit for Confidential and above; analytics otherwise | document, version, user |
| records.label_created / applied / disposition_approved | audit | label, count, actor |
| records.hold_applied / released / deletion_blocked | audit | hold, scope, actor |
| kb.article_feedback | analytics | article, helpful, comment present |

## 14. Security and abuse cases
- Mass download by a departing employee: download rate alerts per user, configurable thresholds, and an alert to security officers (spec 026).
- Malicious files: malware scanning before any preview; previews rendered in a separate sandboxed service; active content (macros, scripts) never executed.
- Watermark removal by screenshot: watermark reduces but does not prevent leaks; the spec makes no stronger claim.
- Permission break widens access by mistake: permission changes on a library or folder with more than 500 documents ask for confirmation and are audited with before and after.
- Legal hold existence is itself sensitive: hold details visible only to records managers; to everyone else a blocked deletion shows "this item cannot be deleted because of a retention rule".

## 15. Non-functional requirements
- Upload throughput: a 100 MB file uploads and is scanned within 60 seconds under normal load.
- Preview for a 50-page Office document is ready within 10 seconds of first request, then cached per version.
- Library view of 100,000 items with metadata filter p95 under 1.5 seconds.
- Permission changes reflect in search within the KNO-006 objective.

## 16. Success criteria
- **SC-001**: An auditor's question "which version of SOP-123 was in force on a given date, who approved it and who confirmed reading" is answered by a records manager in under 5 minutes.
- **SC-002**: No reader ever sees a draft or superseded version as current in acceptance tests.
- **SC-003**: Every deletion path is blocked for held items, proven by one automated test per path.
- **SC-004**: A pilot tenant migrates a 10,000-file share with metadata through bulk import in one working day.

## 17. Open questions
- [NEEDS CLARIFICATION: qualified e-signature for government customers?] Default: connector in R3.
- [NEEDS CLARIFICATION: should TeamBeats libraries be optional when SharePoint is the system of record?] Default: yes; tenants can run read-only over SharePoint and keep controlled documents in TeamBeats.
- [NEEDS CLARIFICATION: default storage quota per package] Default: set commercially (C1 in open questions).

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| KNO-001 | FR-001, FR-011, US1, US4 |
| KNO-003 | FR-002, FR-019, FR-020 |
| KNO-006 | FR-014, US6 |
| ADM-003 | FR-013, FR-015, US6, US7 |
| UC-14 (deletion and legal hold) | FR-015, US7 |

## Review checklist
- [x] No implementation details
- [x] Every FR is testable and traces to a requirement ID or is marked new
- [x] Cross-tenant and org boundary cases are listed
- [x] Entitlement behavior is defined
- [x] Audit events are defined
- [ ] No open [NEEDS CLARIFICATION] items that change scope
