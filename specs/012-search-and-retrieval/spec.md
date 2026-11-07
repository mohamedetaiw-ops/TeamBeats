# Feature Specification: Search and Retrieval

| Field | Value |
|---|---|
| Spec ID | 012-search-and-retrieval |
| Area | knowledge |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (one search across people, content, documents, knowledge articles, policies and services; type-ahead; keyword and semantic hybrid ranking; permission filtering at query time; best bets; the retrieval contract used by the assistant; the 5 minute synchronization objective), R2 (R2 sources: communities, courses and project spaces; federated results from SharePoint and other connected systems; AI summary of top results; saved searches with alerts; search analytics dashboards in 021; judged query sets), R3 (personalized ranking) |
| Last updated | 2026-11-07 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | KNO-002 to KNO-006, SRC-001 to SRC-022 (new) |
| Use cases | UC-05, UC-06 |
| Depends on | 004 (authorization), 005, 006, 007, 009, 010, 011, 013 (embeddings), 016, 025, 034 (gate on retrieval changes) |
| Used by | 014 (assistant retrieval), 020 and 027 (knowledge tools), 021 (search analytics, R2) |

## 1. Problem and outcome

**Problem:** Employees cannot find what they need across news, pages, documents, policies, people and services, and the assistant needs a retrieval layer that never shows content a person cannot read. Specs 009, 011 and 025 already rely on this spec for a synchronization objective: permission changes and deletions must reach the index within 5 minutes.

**Outcome:** One search box returns permitted results from every TeamBeats source, ranked by a mix of keyword and meaning, in Arabic and English. The same index and permission filter serve the assistant's retrieval. Permission changes and deletions reach the index within 5 minutes by default.

## 2. Scope

**In scope:**
- Sources in R1: people (010), content items (009), documents and knowledge articles with FAQ pairs as separate units (011), policy clauses (025), services (016).
- Index records carrying source, item ID, version, language, classification, org unit, audience facts and the permission facts needed to filter at query time. *(KNO-003)*
- Keyword retrieval with Arabic and English text handling, semantic retrieval with embeddings from the AI Gateway, and hybrid ranking. *(KNO-004, KNO-005)*
- Permission filtering at query time through the central authorization service, never only at index time. *(KNO-002)*
- Type-ahead suggestions, filters by source and date, sorting, and result previews.
- Best bets: administrators pin a result to queries.
- A retrieval contract for the assistant and agents: top passages with citation anchors, filtered as the user.
- Synchronization objective: permission changes and deletions reflected within 5 minutes by default. *(KNO-006)*

- R2: sources from communities (023), courses (028) and project spaces (029), each with its own access rules.
- R2: federated results from SharePoint libraries (011 connected libraries) and connectors that implement the 017 search contract, queried live as the user, shown in their own group.
- R2: AI summary of top results with citations through the assistant (014), off by default per tenant.
- R2: saved searches with alerts for new results through 015.
- R2: judged query sets per tenant for ranking evaluation (034) and the search analytics feed for 021.

**Out of scope:**
- Personalized ranking (R3).
- Indexing content of connected systems into the TeamBeats index (federated results are queried live, never copied).
- Searching email and chats.

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Search and see permitted results | Active membership; each result checked against the item's permissions |
| Search administrator | Manage best bets, synonyms, federated sources, judged query sets and the AI summary setting | `search.manage` at tenant scope |
| Search analyst | See search analytics (spec 021) | `analytics.view` with the search dataset |
| Assistant and agents | Retrieve passages as the user | Spec 027 tool grant plus the user's permissions |

## 4. User scenarios

### User Story 1: Search everything I may see (Priority: P1, R1)

Omar types "travel policy" and sees the Travel policy clause, the travel request service, a news item about new travel rules and the Finance contact who owns travel claims. A colleague in another unit without access to the Finance library sees the same search without the Finance documents.

**Independent test:** index items from each source with different permissions; search as several users; check results, counts and filters.

**Acceptance scenarios:**
1. **Given** items in each source, **When** a user searches, **Then** results from all sources appear in one ranked list with source labels, and filters by source narrow it.
2. **Given** an item the user cannot read, **Then** it never appears, does not count in totals or facet counts, and is never suggested in type-ahead.
3. **Given** an Arabic query, **Then** Arabic content matches with normalization of letter variants and diacritics; an English query matches English content; a query in one language can match the other through meaning when semantic retrieval is on.
4. **Given** a superseded policy version or a minor document version the user cannot see, **Then** it is not returned by default.
5. **Given** a query with no results, **Then** the page says so and suggests services or people where they match.

### User Story 2: Changes reach search quickly (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a document's permission is narrowed, **When** 5 minutes pass, **Then** users who lost access no longer get it in search or retrieval. Because results are also checked at query time, they never open it.
2. **Given** an item is deleted or unpublished, **Then** it is gone from search and retrieval within 5 minutes.
3. **Given** the indexer is behind, **Then** the lag is visible to operators and an alert fires when it passes the objective.
4. **Given** a full rebuild, **Then** the index is rebuilt from the owning modules without data loss, because the index is a derived read model.

### User Story 3: Retrieval for the assistant (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a question, **When** the assistant retrieves, **Then** it gets the top passages the user may read, each with source, version, language, classification and a citation anchor. *(AI-005, KNO-007)*
2. **Given** a passage whose classification the AI policy blocks for the use case, **Then** it is not returned for AI use, and the assistant is told evidence was withheld by policy.
3. **Given** a change to retrieval configuration (embedding model, chunk size, number of results), **Then** it runs through the spec 034 release gate against a shadow index before it applies.

### User Story 4: Best bets and synonyms (Priority: P2, R1)

**Acceptance scenarios:**
1. **Given** a best bet for "payslip" pointing to the payslip service, **When** a user who may see the service searches "payslip", **Then** it shows first, marked as recommended.
2. **Given** a synonym pair ("leave", "vacation", "إجازة"), **Then** queries with one find items with the other.

### User Story 5: Find files in SharePoint too (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a SharePoint connected library (011) with federated search on, **When** Omar searches, **Then** a "SharePoint" group shows results queried live with his delegated identity, so SharePoint enforces its own permissions.
2. **Given** SharePoint is slow, **Then** TeamBeats results show at once and the federated group loads after, with a 3 second limit and a "not available now" note on timeout.
3. **Given** a federated source the tenant turned off, **Then** no query is sent to it.

### User Story 6: Get a short answer above results (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** the tenant turned on AI summaries, **When** Laila searches a question, **Then** a short summary from the top permitted passages appears above results with citations, produced through 014 and 013.
2. **Given** the top passages are blocked for AI by classification, **Then** no summary is shown and results appear as usual.
3. **Given** the summary is wrong, **Then** Laila can mark it unhelpful, which feeds 034 production evaluation.

### User Story 7: Be told when something new matches (Priority: P3, R2)

**Acceptance scenarios:**
1. **Given** Laila saved the search "procurement policy", **When** a new item she may read matches, **Then** she gets at most one grouped notification a day.
2. **Given** the item is one she cannot read, **Then** no alert is sent.

### User Story 8: Measure and improve ranking (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a judged query set (queries with the results that should rank high), **When** ranking or retrieval configuration changes, **Then** the 034 gate reports precision at 5 and NDCG at 10 against the set and blocks a regression beyond the threshold.
2. **Given** search events, **Then** spec 021 shows top queries, zero result queries and click-through, with query text hidden for tenants that turned it off.

### Edge cases
- An item in several languages: each variant is a separate unit with its language, grouped in results.
- Very large documents: indexed in passages with a per-document limit; the rest is searchable by keyword only.
- Embedding provider unavailable: keyword search keeps working and semantic results show a degraded note.

## 5. Functional requirements

- **SRC-001 / FR-001**: Search MUST cover the R1 sources in section 2 in one query. *(KNO-002)*
- **SRC-002 / FR-002**: Every result MUST be checked against the user's permissions at query time; totals, facets and suggestions MUST count only permitted items. *(KNO-002, constitution VII)*
- **SRC-003 / FR-003**: Index records MUST carry source, version, language, classification, org unit and synchronization time. *(KNO-003)*
- **SRC-004 / FR-004**: Semantic retrieval MUST use pgvector with embeddings from the AI Gateway under the tenant's AI policy. *(KNO-004)*
- **SRC-005 / FR-005**: Keyword retrieval MUST support Arabic normalization and English stemming, combined with semantic scores by hybrid ranking. *(KNO-005)*
- **SRC-006 / FR-006**: Permission changes and deletions MUST be reflected within 5 minutes by default, with lag monitoring and an alert. *(KNO-006)*
- **SRC-007 / FR-007**: The retrieval contract MUST return passages with citation anchors, filtered as the user and by AI classification policy. *(KNO-007, AI-005)*
- **SRC-008 / FR-008**: Retrieval configuration changes MUST pass the spec 034 gate before they apply.
- **SRC-009 / FR-009**: Type-ahead MUST return suggestions within 150 ms p95, permission filtered.
- **SRC-010 / FR-010**: Administrators MUST be able to manage best bets and synonyms per locale.
- **SRC-011 / FR-011**: Superseded and non-effective versions MUST be excluded by default.
- **SRC-012 / FR-012**: The index MUST be rebuildable from the owning modules at any time.
- **SRC-013 / FR-013**: Search MUST record query events (pseudonymous, spec 021) for later analytics, without storing query text for tenants that turn it off.
- **SRC-014 / FR-014**: When embeddings are unavailable, keyword search MUST keep working with a degraded note. *(constitution XII)*
- **SRC-015 / FR-015**: Communities, courses and project spaces MUST be indexed with their own access rules, including closed and secret visibility (R2).
- **SRC-016 / FR-016**: Federated results MUST be queried live with the user's delegated identity, MUST NOT be stored in the TeamBeats index, and MUST time out after 3 seconds without delaying TeamBeats results.
- **SRC-017 / FR-017**: Federated sources MUST be per tenant settings using 011 connected libraries or connectors implementing the 017 search contract.
- **SRC-018 / FR-018**: AI summaries MUST use only passages from the retrieval contract for the user, MUST cite them, and MUST be off by default.
- **SRC-019 / FR-019**: Saved search alerts MUST notify through 015 at most once a day per saved search and only for permitted items.
- **SRC-020 / FR-020**: Judged query sets MUST be usable by the 034 gate with precision at 5 and NDCG at 10.
- **SRC-021 / FR-021**: Payslips (019), anonymous engagement items (023) and other items their owning spec excludes MUST never be indexed.
- **SRC-022 / FR-022**: Guests (026) MUST search only within spaces they were invited to.

## 6. Business and validation rules
- Default results per page 20; maximum query length 200 characters.
- Classification ceiling for AI retrieval follows the tenant's AI policy for the calling use case (spec 013).

## 7. Tenant and organization scope
- Index records are tenant-owned with RLS; embeddings never mix tenants.
- Org boundary: results follow each item's own permissions; a unit's private library is invisible to sibling units.
- Cross-tenant denial: no query, suggestion or retrieval returns another tenant's items, even with identical text.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Index record and passage | Derived from owning modules | Same as the item | Until the item changes or is deleted | Possibly (people) |
| Embedding | Derived | Same as the item | Same as the record | No |
| Best bets, synonyms | Product | Internal | Life of tenant | No |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `search.core` (keyword, permission filtering, best bets) | on | on | on |
| `search.semantic` (embeddings count against the AI quota) | on | on | on |
| `search.federated` (R2) | off | on | on |
| `search.ai_summary` (R2) | off | on | on |
| `search.saved_alerts` (R2) | on | on | on |

## 10. UX, localization and accessibility
- One search box in the shell (spec 008) with type-ahead; a results page with filters.
- Results announce counts to screen readers; filters are keyboard operable; RTL layout.

## 11. AI behavior
Embeddings are produced through the AI Gateway with the item's classification. Restricted items are embedded only by an approved private endpoint (none in R1, so they are keyword-only). Query text sent for embedding is classified Internal.

## 12. Integrations and events
Consumes (R2 additions: `PostPublished`, `PostRemoved`, `CourseVersionPublished`, `SpaceCreated`, `TaskAssigned`) and `ContentPublished`, `ContentUnpublished`, `DocumentVersionPublished`, `DocumentDeleted`, `DocumentPermissionsChanged`, `PolicyPublished`, `ServicePublished`, `ProfileUpdated`, `ResourcePermissionChanged`, `RoleAssignmentChanged`, `OrganizationMembershipChanged`. Publishes none.

## 13. Audit

| Event | Type | Key fields |
|---|---|---|
| search.best_bet_changed | audit | query, target, actor |
| search.synonyms_changed | audit | locale, actor |
| search.rebuild_started / completed | operational | tenant, counts |

## 14. Security and abuse cases
- Inferring hidden items from counts or suggestions: counts and suggestions are permission filtered.
- Prompt injection through indexed content: passages are returned as untrusted data (spec 027).
- Stale permissions in the index: query-time checks close the gap.

## 15. Non-functional requirements
- Search p95 under 500 ms for 1 million index units per tenant.
- Index lag p95 under 1 minute, objective 5 minutes.

## 16. Success criteria
- **SC-001**: No test fixture the user cannot read ever appears in results, counts or suggestions on any build.
- **SC-002**: In the pilot, 80% of searches end with a click on a result in the first page.

## 17. Open questions
- [NEEDS CLARIFICATION: is a dedicated search engine (for example OpenSearch) needed for scale?] Default: PostgreSQL full text and pgvector in R1, revisit with measured load.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| KNO-002 | SRC-001, SRC-002, US1 |
| KNO-003 | SRC-003 |
| KNO-004 | SRC-004 |
| KNO-005 | SRC-005 |
| KNO-006 | SRC-006, US2 |
| KNO-007, AI-005 | SRC-007, US3 |
| R2 additions | SRC-015 to SRC-022, US5 to US8 |
