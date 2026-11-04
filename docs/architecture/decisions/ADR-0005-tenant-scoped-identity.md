# ADR-0005: Tenant-scoped user identities

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-11-04 |
| Owners | Mohamed |
| Related specs | 003, 004 |

## Context and constraints
The foundation's entity sketch had a global `UserIdentity` table and left cross-tenant identity as an open business question (decision 6). A global table in a shared installation holds personal data from every customer outside Row-Level Security.

## Options considered
1. **Global identity, per-tenant memberships**: one person can switch tenants, but a cross-tenant personal data table exists and every query on it is a leak risk.
2. **Tenant-scoped identity**: each tenant has its own user records keyed by (tenant, issuer, subject).

## Decision
Option 2, decided by Mohamed on 2026-11-04. There is no global user or person record. The same human in two tenants is two unrelated users. Any future cross-tenant linking needs a new ADR.

## Consequences and risks
- No tenant switcher for a single login. A person working for two customers signs in to each separately.
- Simpler isolation: user tables are tenant-owned and covered by RLS like everything else.
- Domain-to-tenant mapping at sign-in must be unique per installation.

## Security and multi-tenancy implications
Removes the main exception to "every tenant-owned table has tenant_id and RLS".

## Rollout and migration plan
Applies from the first migration in R0.
