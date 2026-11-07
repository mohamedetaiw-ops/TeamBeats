# Tasks: Search and Retrieval

Depends on 004, 009, 010, 011, 013, 016, 025, 034. R1: US1 to US4. R2: US5 to US8.

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

## R2
- [ ] T011 Indexing consumers for 023, 028 and 029 with their access rules; exclusions of SRC-021; guest scope
- [ ] T012 [P] [US5] Tests: delegated live query, 3 second budget, source off sends nothing
- [ ] T013 [US5] Federated search contract and SharePoint source
- [ ] T014 [US6] AI summary with citations, off by default
- [ ] T015 [US7] Saved searches and daily alerts
- [ ] T016 [US8] Judged query sets and search scorer in 034; analytics feed for 021
