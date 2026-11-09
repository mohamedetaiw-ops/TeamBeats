# Spec 012 evidence (R1)

The spec stays Approved, not Implemented. R1 still lacks the performance tests at 1 million units with paged rebuilds (T010). The R2 parts are not built either: communities, courses and projects (FR-015), federated search (FR-016, FR-017), AI summaries (FR-018), saved search alerts (FR-019), judged query sets (FR-020) and guests (FR-022). The spec 012 section of `docs/architecture/r1-implementation-notes.md` holds the defaults, deviations and gaps.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 one query across the R1 sources | Met | `SearchTests.News_is_found_in_english_and_arabic_and_every_number_counts_only_what_the_reader_may_open`, `Documents_policy_clauses_and_services_are_found_by_people_who_can_open_them` |
| FR-002 permission check at query time, counts and suggestions included (SC-001) | Met | The same two `SearchTests`, which assert on results, totals, facets and suggestions on every CI build |
| FR-003 index records carry source, version, language, classification, unit and sync time | Met | `search.unit` (migration 0001), `SearchTests` |
| FR-004 pgvector embeddings through the AI Gateway, Restricted keyword only | Met | `SearchIndexer`, `SearchTests.Search_falls_back_to_keyword_only_when_embeddings_fail` |
| FR-005 Arabic normalization and English stemming, fused with semantic results | Met | `SearchTextTests` (unit), `SearchTests` in Arabic and English |
| FR-006 changes and deletions within 5 minutes, lag monitoring and alert | Met | Deletions and unpublishing are applied by the next consumer run (`SearchTests`, "Unpublished news leaves the index"). Permission changes apply on the next query, because the owner checks every candidate. Lag: `SearchOperationsTests.Indexing_after_the_objective_is_counted_and_query_events_never_keep_the_query` (`messaging.consumer.lag` and `lag_exceeded`) |
| FR-007 retrieval contract with anchors and the AI classification filter | Met | `SearchTuningTests.Retrieval_returns_readable_passages_with_anchors_and_withholds_what_the_ai_policy_blocks`, `PolicyQuestionTests` |
| FR-008 retrieval configuration through the spec 034 gate (US3 scenario 3) | Met | `SearchRetrievalGateTests` (a passing gate applies and replaces the live index; an incomplete gate holds for a reason; a forbidden source rejects; the shadow index follows owner changes; only search administrators of the tenant; cancel; late decisions change nothing), `search.spec.ts` (propose, held, apply with a reason, en and ar, axe) |
| FR-009 type-ahead within 150 ms p95, permission filtered | Met for filtering | `SearchTests` (suggestions hold only readable items); a load test for suggest is part of the open performance work in T010 |
| FR-010 best bets and synonyms per locale | Met | `SearchTuningTests` (best bets, synonyms), `search.spec.ts` |
| FR-011 superseded versions excluded | Met | `SearchTests`, `PolicyQuestionTests` (no superseded clause after a revision) |
| FR-012 rebuild from the owners | Met | `SearchTests.A_rebuild_needs_search_manage_and_indexes_every_owner_including_people` |
| FR-013 pseudonymous query events | Met | `SearchOperationsTests` (first page only, results and filter, never the query text) |
| FR-014 keyword search with a degraded flag when embeddings fail | Met | `SearchTests.Search_falls_back_to_keyword_only_when_embeddings_fail` |
| Section 7 cross-tenant cases | Met | `SearchTests` (another tenant finds nothing), `SearchTuningTests` |
| Pages (T009) | Met | `search.spec.ts` (en and ar, axe) |
