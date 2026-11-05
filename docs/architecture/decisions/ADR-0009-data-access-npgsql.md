# ADR-0009: Data access with Npgsql and explicit SQL (no EF Core in R0)

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-04 |
| Owners | Mohamed |
| Related specs | 001 to 007 (all R0 persistence) |
| Supersedes | The EF Core line in `docs/architecture/r0-solution-structure.md` sections 1 and 3 |

## Context and constraints
The R0 layout planned EF Core with a connection interceptor that sets `app.tenant_id`. The non-negotiables are: tenant context set per transaction, RLS forced on every tenant table, the app role never owning tables, audit rows append-only, and closure-table hierarchy queries. Most R0 queries are set-based (closure inserts and moves, effective grants, audit sealing through SECURITY DEFINER functions) and are clearer in SQL than in LINQ.

## Decision drivers
Tenant isolation that is easy to review, predictable SQL for RLS and closure tables, few dependencies (constitution XXII), and migrations a DBA can read.

## Options considered
1. **EF Core with interceptors**: familiar, change tracking and LINQ. But the hierarchy, grants and audit queries would be raw SQL anyway, migrations are generated C# rather than reviewable SQL, and an interceptor that sets tenant context is one more place where context can be lost (for example on a connection opened outside a transaction).
2. **Npgsql with a small SQL helper and a unit of work**: one connection and one transaction per request or message; tenant context is set when the transaction opens, so no query can run without it. Migrations are plain SQL files with checksums.
3. **Dapper**: small and popular, but adds a dependency for what a 150-line helper does.

## Decision
Option 2. `IDbSession` opens the transaction and sets `app.tenant_id` with `set_config(..., true)`. Handlers commit explicitly; anything not committed rolls back. Each module ships ordered SQL migrations run by `MigrationRunner` as `tb_owner` under an advisory lock.

## Consequences and risks
- No change tracking: updates are explicit SQL. Optimistic concurrency uses a `row_version` column where specs need it.
- More SQL to review. Mitigated by the security suite (forced RLS on every tenant table, cross-tenant route sweep) and integration tests on real PostgreSQL.
- If a later module benefits from EF Core (for example rich content editing), it can adopt it inside its own boundary with a new ADR, using the same connection and transaction.

## Security and multi-tenancy implications
Tenant context cannot be skipped: `IDbSession` is the only way modules reach the database, and the security suite checks that a connection without context sees no rows and that context does not survive in the pool.

## Rollout and migration plan
Already in place for R0. No data migration needed. Merging the R0 pull request is acceptance of this ADR, the same way merging PR #1 accepts ADR-0006 to ADR-0008.
