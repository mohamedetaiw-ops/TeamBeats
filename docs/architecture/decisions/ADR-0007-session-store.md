# ADR-0007: Server-side session store

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-04 |
| Owners | Mohamed |
| Related specs | 003, 001 (suspension), 006 (disable membership) |

## Context and constraints
Spec 003 requires immediate session revocation when a user is disabled, revocation when a tenant is suspended, role changes effective within 5 minutes, and no tokens in the browser. Redis is optional in the foundation and should not be mandatory for on-prem.

## Options considered
1. **Stateless encrypted cookie (ASP.NET default)**: no server lookup, but revocation is impossible before expiry. Fails spec 003.
2. **Redis session store**: fast, but adds a mandatory component to every deployment.
3. **PostgreSQL session table with a short in-memory cache and event-driven eviction**.

## Decision (proposed)
Option 3. The cookie carries an opaque session ID. Sessions are tenant-owned rows protected by RLS. Each API instance caches a session for up to 30 seconds; `SessionRevoked`, `MembershipDisabled` and `TenantSuspended` events evict immediately across instances. Redis can be added later behind the same interface if load requires it (ADR needed).

## Consequences and risks
- One indexed read per request on cache miss; acceptable at R0/R1 scale.
- Expired session cleanup job in the worker host.

## Security and multi-tenancy implications
Session lookups run with tenant context resolved from the host first, so a session ID from tenant A presented on tenant B's host finds nothing.

## Rollout and migration plan
R0 task 003/T002.
