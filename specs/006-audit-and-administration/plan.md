# Implementation Plan: Audit Trail and Admin Shell

**Spec:** `specs/006-audit-and-administration/spec.md` (v1.0, Approved) | **Branch:** `006-audit-admin` | **Date:** 2026-11-04
**Shared base:** `docs/architecture/r0-solution-structure.md`

## Summary
An `Audit` module with an append-only per-tenant audit table written in the caller's transaction through `IAuditWriter`, a background sealer that hash-chains records per tenant (ADR-0008), scoped search, asynchronous export, and retention purge. The admin shell is a Next.js layout with a server-driven navigation model from `GET /api/v1/admin/navigation`, so menus only show areas the backend authorizes.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| VI Audit | Yes | Append-only: `tb_app` has INSERT and SELECT only; no UPDATE/DELETE grant. Purge runs as a dedicated role through a SECURITY DEFINER function that also writes a purge record |
| I | Yes | `audit_event` tenant-owned with RLS; `platform_audit_event` separate table |
| IV | Yes | Writer API takes typed change summaries; secret fields marked `[Sensitive]` are excluded automatically |
| III | Yes | Navigation and every admin endpoint use `IAuthorizationService` |

## Data model (schema `audit`)
- `audit_event`: `tenant_id`, `id`, `occurred_at`, `actor_id`, `actor_type`, `action`, `target_type`, `target_id`, `target_org_unit_id null`, `outcome`, `correlation_id`, `source_ip`, `changes jsonb`, `chain_pos bigint null`, `prev_hash null`, `hash null`, `sealed_at null`. Partitioned by month on `occurred_at`.
- `audit_chain_state`: `tenant_id`, `last_chain_pos`, `last_hash`.
- `audit_retention`: `tenant_id`, `days` (min 365).
- `audit_export_job`: `tenant_id`, `id`, `filter jsonb`, `status`, `object_key`, `expires_at` (7 days).

## Sealing (ADR-0008 proposal)
Writes do not compute hashes (avoids per-tenant write contention). The worker seals committed unsealed rows per tenant in (`occurred_at`, `id`) order, assigning `chain_pos` at sealing time and setting `hash = SHA-256(prev_hash || canonical_json(row))` via a narrowly granted function that may only fill these columns once (see ADR-0008). Integrity check recomputes the chain and reports the first break.

## Search
`GET /api/v1/audit/events?actor=&action=&targetType=&from=&to=&cursor=` with keyset pagination. Org admins get an added predicate `target_org_unit_id IN (their subtree)`. Rate limited per user.

## Export
`POST /api/v1/audit/exports` creates a job (Tenant Admin or Security Officer only), worker streams CSV or JSON to `tenants/{tenant_id}/exports/audit/{job}`; download via short-lived link; the export is itself audited.

## Admin shell
- Layout with sections: Tenant settings, Organization, Users and groups, Roles, Plan and usage, Identity, Audit.
- Users list and detail: memberships, roles, sign-in status, disable membership (calls Tenancy contract; Identity revokes sessions on event).

## Tests
- Append-only enforced (UPDATE/DELETE as `tb_app` fails).
- Tamper detection: modify a sealed row as owner, integrity check reports position.
- Rolled-back transaction leaves no audit row.
- Scoped search for org admin; cross-tenant search returns nothing.
- Export audited; non-authorized role denied.
- Admin URL as employee: page shows no-access, API 403/404.
- Every audit event named in specs 001 to 005 asserted by a contract test listing.
