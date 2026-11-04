# ADR-0002: PostgreSQL shared schema with Row-Level Security for tenant isolation

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-04 |
| Owners | Mohamed |
| Related specs | 001, 002, 004, 006 |

## Context and constraints
The product must support shared installations (many tenants), dedicated installations, and customer on-prem. Cross-tenant leakage is the most serious possible failure.

## Decision drivers
Strong isolation, one data model for all deployment profiles, manageable migrations, defense in depth.

## Options considered
1. **Database per tenant everywhere**: strongest isolation, heavy operations and migrations at scale.
2. **Schema per tenant**: migration and connection overhead, weak tooling support.
3. **Shared schema with `tenant_id` and RLS**, plus database per tenant only where contracts require it.

## Decision
Option 3, with these mandatory rules:
- Every tenant-owned table has `tenant_id NOT NULL`, tenant-scoped unique constraints, and composite foreign keys including `tenant_id` where cross-row references exist.
- RLS enabled and forced on tenant-owned tables. Policies compare `tenant_id` to `current_setting('app.tenant_id', true)`; a missing setting matches no rows.
- The application connects with a role that does not own tables and has no BYPASSRLS. Migrations run with a separate owner role. Platform operator queries use a third role through an audited break-glass path.
- Tenant context is set with `set_config('app.tenant_id', ..., true)` at the start of every transaction, including read transactions. Workers set it per message.
- Dedicated and on-prem deployments keep `tenant_id` and RLS unchanged.

## Consequences and risks
- Every query path needs a transaction; this is enforced by the data access building block.
- RLS adds some query overhead; indexes lead with `tenant_id`.
- Large shared vector indexes may need partitioning by tenant later (pgvector filtering behaviour), to be revisited in the search ADR.

## Security and multi-tenancy implications
RLS is defense in depth, not a replacement for application authorization (spec 004). Automated cross-tenant negative tests are mandatory for every data path (constitution XXVIII).

## Rollout and migration plan
Implement the tenant context building block, RLS migration helpers and the negative-test harness in Phase 1 before feature tables.
