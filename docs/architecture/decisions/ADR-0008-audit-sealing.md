# ADR-0008: Tamper-evident audit by asynchronous hash-chain sealing

| Field | Value |
|---|---|
| Status | Accepted (2026-11-05 by Mohamed) |
| Date | 2026-11-04 |
| Owners | Mohamed |
| Related specs | 006 |

## Context and constraints
Constitution VI and spec 006 require append-only audit with tamper detection. Audit writes happen inside many business transactions; computing a hash chain at write time forces all writes for a tenant to serialize on the previous hash.

## Options considered
1. **Hash at write time**: simplest chain, but per-tenant write contention and deadlock risk under load.
2. **No chain, append-only grants only**: protects against the application, not against a database owner.
3. **Asynchronous sealing**: rows are inserted unsealed with a per-tenant sequence number; a worker seals them in order.
4. **External ledger or WORM storage**: strongest, but adds infrastructure.

## Decision (proposed)
Option 3. Rows are inserted with `chain_pos`, `prev_hash` and `hash` empty. The sealer worker, per tenant, takes all committed unsealed rows (ordered by `occurred_at`, then `id`), assigns the next `chain_pos` values in that order, and sets `hash = SHA-256(prev_hash || canonical_json(row))`, through a function that can only fill these columns once. The chain order is the sealing order, so a transaction that commits late is simply sealed in the next batch. An integrity check recomputes the chain and reports the first break. Periodic chain heads can later be exported to customer SIEM or WORM storage (option 4 as an add-on).

## Consequences and risks
- A short window (seconds) where newest rows are unsealed; integrity reports show sealed coverage.
- Because positions are assigned at sealing time, rolled-back or late-committing transactions never create gaps in the chain. Any gap or reordering in `chain_pos` is therefore a real integrity failure.

## Security and multi-tenancy implications
Chains are per tenant, so one tenant's verification never reads another tenant's rows. The sealer runs with tenant context per batch.

## Rollout and migration plan
R0 task 006/T005.
