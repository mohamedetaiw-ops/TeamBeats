# Implementation Plan: Search and Retrieval

**Spec:** `specs/012-search-and-retrieval/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0002 (PostgreSQL), ADR-0013, spec 013 plan (embeddings), module map (`Search`, derived read model)

## Summary
A `Search` module owns a derived index in PostgreSQL: `search.unit` rows (one per searchable passage) with a `tsvector` for keyword search (Arabic normalized text in a `simple` configuration after normalizing alef, ya, ta marbuta and removing diacritics; English with the `english` configuration), an embedding `vector` column (pgvector, HNSW index) filled through `IAiGateway.EmbedAsync`, and permission facts. No new service; PostgreSQL 18 with pgvector is already the platform database.

## Permission filtering
Two stages: the index stores coarse facts (tenant through RLS, org unit, audience, resource ID and owning module); the query fetches more candidates than needed (3 times the page size) and filters them with the owning module's batch check (`IAuthorizationService` batch filter or the module's `CanViewMany` contract). Totals and facets come from the filtered set, capped at 1,000 with "more than" text.

## Indexing
Consumers of the events in spec section 12 fetch the item through the owner's contract as a system reader, split it into passages (default 400 tokens with 15% overlap; FAQ pairs and policy clauses as their own units), upsert units and enqueue embeddings. Permission events re-stamp facts only. A lag metric per tenant feeds an alert above 5 minutes. A rebuild job pages through each owner.

## Ranking
Hybrid with reciprocal rank fusion of keyword and vector lists, then best bets on top. Retrieval configuration (chunk size, overlap, top K, embedding model) is a versioned tenant record; a change builds a shadow index and asks the spec 034 gate before switching.

## API (v1)
`GET /api/v1/search?q=&source=&from=&to=&page=`, `GET /api/v1/search/suggest?q=`, best bets and synonyms admin endpoints; `ISearchRetrieval.RetrieveAsync(query, filters, useCase)` in `Search.Contracts` for the assistant and agent tools.

## R2
- New sources are more consumers of the same indexing pipeline.
- Federated search: `IFederatedSearchSource` in `Integrations.Contracts`, implemented by the SharePoint adapter (Graph search API with delegated tokens) and connector packs; queried in parallel with a 3 second budget and returned as a separate group from a second endpoint so the main results are not delayed.
- AI summary: the results page calls the 014 assistant contract with the retrieved passages.
- Saved searches: a daily job runs each saved query as the user and sends one grouped notification.
- Judged sets: stored in `Evaluation` (034) as a dataset type, scored by a search scorer.

## Tests
Permission fixtures across tenants, units and broken inheritance (SC-001 on every build); Arabic normalization; lag and deletion within the objective; degraded keyword-only mode; rebuild; performance at 1 million units. Acceptance (e2e): search box, results and filters in English and Arabic with axe.
