# Tasks: Search and Retrieval

Depends on 004, 009, 010, 011, 013, 016, 025, 034. All R1 user stories.

- [ ] T001 `Search` and `Search.Contracts` projects; migration for units with tsvector and vector columns, best bets, synonyms and retrieval configuration (forced RLS)
- [ ] T002 Permission `search.manage`; audit actions; capability keys
- [ ] T003 [P] Unit tests: Arabic normalization, passage splitting, rank fusion
- [ ] T004 Indexing consumers per source with passages and embeddings; lag metric and alert; rebuild job
- [ ] T005 [P] [US1] [US2] Tests: one list across sources; hidden items absent from results, counts and suggestions; superseded excluded; deletion and permission change within 5 minutes; keyword-only when embeddings fail; cross-tenant
- [ ] T006 [US1] Search and suggest endpoints with two-stage permission filtering
- [ ] T007 [US3] Retrieval contract with citation anchors and AI classification filter; retrieval configuration with shadow index and gate
- [ ] T008 [US4] Best bets and synonyms
- [ ] T009 Search box in the shell, results page with filters, admin pages, in English and Arabic, RTL; e2e with axe
- [ ] T010 SC-001 suite in CI; performance tests; OpenAPI; R1 implementation notes; spec evidence
