# ADR-0020: Full-text search index at scale under forced row-level security

| Field | Value |
|---|---|
| Status | Proposed (2026-11-10). Option 3 changes a constitution rule, so it waits for Mohamed's decision |
| Date | 2026-11-10 |
| Owners | Mohamed |
| Related specs | 012 (section 13: search p95 under 500 ms at 1 million units per tenant; FR-009: type-ahead under 150 ms p95), 014, 020; constitution I and II (forced RLS); ADR-0013 |

## Context and constraints
Spec 012 sets a search p95 under 500 ms and type-ahead under 150 ms at 1 million index units in one tenant. Keyword matching uses a PostgreSQL `tsvector` column with a GIN index on `search.unit`, which has forced row-level security like every tenant table (constitution: "Row-Level Security MUST be enabled and forced on tenant-owned tables").

Measured on 2026-11-10 with 1 million synthetic units in one tenant (PostgreSQL 16):

| Query | Rows matched | Time | Plan |
|---|---|---|---|
| Keyword candidates for one common word | 130,851 | 1,072 ms | sequential scan |
| Keyword candidates for two words | 11,703 | 769 ms | sequential scan |
| Type-ahead for a 3 letter prefix | 97,439 | 962 ms | sequential scan |
| A word that matches nothing | 0 | 658 ms | sequential scan; with sequential scans disabled, the planner still cannot use the GIN index |

The GIN index is never used, because the match operator (`ts_match_vq`) is not marked leakproof. Under row-level security, PostgreSQL evaluates only leakproof conditions before the policy condition, so a non-leakproof condition cannot become an index condition. Every search therefore reads all of the tenant's units. At the same rate a tenant with 100,000 units would take about 70 to 100 ms (an estimate, not measured), which covers the pilot; at 1 million it misses both budgets.

Only a superuser can mark a function leakproof. Managed PostgreSQL services usually do not give one. The application role must keep its row-level security.

## Options considered
1. **Keep the sequential scan.** No change. Search time grows linearly with a tenant's index, about 0.7 s per million units. Meets the budgets for tenants up to roughly 300,000 units.
2. **Mark the text search match functions leakproof at install.** A superuser runs `alter function ts_match_vq(tsvector, tsquery) leakproof` (and the matching operators). The GIN index is then used and the rest stays as it is. This does not work on managed services without a superuser. It also claims a property the PostgreSQL project has not reviewed for these functions.
3. **Search through security definer functions on an RLS table that is not forced (recommended).** `search.unit` keeps row-level security enabled, so the application role still sees only its tenant on every direct query, but it is not forced, so its owner is exempt. Two SQL functions owned by the table owner, `search.keyword_candidates` and `search.suggest`, take the query and read units with an explicit `tenant_id = platform.current_tenant_id()` condition. They return nothing when the session has no tenant. The GIN index (with `tenant_id` added through `btree_gin`, which is a trusted extension) serves them. The application role gets `execute` on the functions only. This needs a written exception to the constitution's "forced" rule for this one table, and the security test that lists tables without forced RLS records it.
4. **A separate term table with btree indexes.** Text equality and range comparisons are leakproof, so a table of (tenant, term, unit) rows with a btree index works under forced RLS. It needs about one row per distinct word per unit, so roughly 100 million or more rows for a tenant with 1 million passages, plus the writes to keep it current. Too heavy for its benefit.

## Decision
Not yet taken. The recommendation is option 3. It keeps every direct read by the application role under row-level security, puts the tenant condition into two small functions that tests can cover, works on managed PostgreSQL, and meets the spec 012 budgets. Option 1 stays in place until Mohamed decides.

## Consequences of option 3
- The constitution gains a named exception: a table whose only path that bypasses RLS is through owner-defined functions that apply the session tenant themselves. That exception must be listed in `tests/Security/DatabaseIsolationTests` and reviewed like a schema change.
- New tests: cross-tenant denial through both functions; no rows with no tenant context; the plan uses the index at 1 million units; and a p95 test at that size in the backend job.
- The retrieval queries for the assistant and agents (`Retrieval.cs`) move to the same functions.
