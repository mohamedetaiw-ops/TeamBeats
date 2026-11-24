# Implementation Plan: Packages and Entitlements (R0 core)

**Spec:** `specs/005-packages-and-entitlements/spec.md` (v1.0, Approved) | **Branch:** `005-entitlements` | **Date:** 2026-11-04
**Shared base:** `docs/architecture/r0-solution-structure.md`

## Summary
R0 builds the capability catalogue, versioned package definitions seeded from the spec 005 table, tenant subscriptions, and `IEntitlementService.IsEnabled / GetLimit`. Usage metering, quotas, add-ons, overrides, grace and the "Plan and usage" page follow in R2 on the same tables. An ASP.NET Core endpoint filter `[RequiresCapability("key")]` makes gating declarative.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| XIII AI in every package | Yes | Package validation rejects definitions without `ai.assistant` |
| XXI Backend entitlements | Yes | Endpoint filter + service; architecture test bans package keys (`workplace`, `workplace_plus`, `enterprise_gov`) outside this module and seed data |
| I Tenant boundary | Yes | `subscription`, `addon`, `override`, `usage_counter` tenant-owned with RLS; catalogue and package definitions are platform tables (RLS exceptions) |
| VI Audit | Yes | All changes audited to platform audit and tenant audit |

## Data model (schema `entitlements`)
- `capability` (platform): `key`, `type (flag, limit)`, `unit`, `description_key`.
- `package` and `package_version` (platform): `package_key`, `version`, `status (draft, published, retired)`.
- `package_capability` (platform): `package_version_id`, `capability_key`, `enabled`, `limit_value null`.
- `subscription`: `tenant_id`, `package_version_id`, `starts_at`, `ends_at null`, `status`, `expiry_policy (grace_days = 30, then read_only)`.
- R2: `addon`, `override` (with `valid_to`), `usage_counter (tenant_id, capability_key, period, used)`, `enforcement_policy`.

## Service behavior
- `IsEnabled(key)`: tenant from context; resolve subscription -> package version -> add-ons -> overrides (R2); unknown key returns false and logs an error.
- Resolution cached per `(tenant_id, entitlements_version)`; invalidated on `SubscriptionChanged`.
- Denial returns Problem Details `entitlement.not_in_package` (403) distinct from `authz.denied`.
- Expiry (R2 scheduler): after `ends_at`, 30-day grace with admin banner, then read-only mode flag consumed by write endpoints.
- Quotas (R2): atomic `UPDATE usage_counter SET used = used + @n WHERE ... AND used + @n <= @limit + @tolerance RETURNING used`; tolerance = max(1% of limit, 10).

## API (v1)
Platform: `GET/POST /api/v1/platform/packages`, `POST /api/v1/platform/packages/{key}/versions/{v}:publish`, `PUT /api/v1/platform/tenants/{id}/subscription`. Tenant: `GET /api/v1/tenant/entitlements` (read-only).

## Tests
- Allowed, denied, unknown key (fail closed), package without `ai.assistant` rejected, tenant stays on v1 after v2 publish, tenant admin cannot change own subscription, cross-tenant read of entitlements denied.
- R2: quota exhausted, grace, add-on, override expiry, concurrent consumption within tolerance.

## R2: add-ons, overrides, quotas, expiry and the plan page (US3, US4, FR-004, FR-006 to FR-008)
Built on the R0 tables; nothing in feature code changes except where a module meters a quota.

- **Add-ons and overrides (slice 1, T012).** `entitlements.addon` and `entitlements.override` (tenant-owned, RLS): `capability_key`, `enabled`, `limit_value null`, `valid_from`, `valid_to null` (an override needs `valid_to`, a contract always ends), `reason` and who set it; one active row per tenant and key in each table. Resolution per request: package version, then active add-ons, then active overrides (FR-004: overrides win over add-ons, add-ons over the package). A row outside its validity window is ignored, so an override reverts on its end date with no job. Platform operators manage them: `GET/PUT/DELETE /api/v1/platform/tenants/{id}/addons/{key}` and `.../overrides/{key}`. Every change writes `ent.addon_changed` or `ent.override_changed` to the tenant audit and the platform audit, and publishes `EntitlementsChanged` (tenant, changed keys). A tenant cannot reach any of these. The tenant view tells, per capability, where its value comes from (`package`, `addon`, `override`) and until when.
- **Quotas (slice 2, T011).** `entitlements.usage_counter (tenant_id, capability_key, period, used, exhausted_at)` with period `YYYY-MM` in UTC, and `entitlements.enforcement (tenant_id, capability_key, mode, grace_pct)` set by the platform (default `deny`; `grace` allows `grace_pct` over the limit with warnings; `warn` never refuses). `IEntitlementService.ConsumeAsync(key, amount)` runs one atomic `insert ... on conflict do update ... where used + @n <= @ceiling returning used`, where the ceiling is the limit plus the tolerance max(1%, 10) for `deny`, the limit plus `grace_pct` for `grace`, and none for `warn`; a refusal is `entitlement.quota_exhausted` (FR-007). `ReleaseAsync` gives back a reservation a later step refused. Reaching the limit sets `exhausted_at` once per period and publishes `QuotaExhausted`, which writes `ent.quota_exhausted` and notifies the holders of `entitlements.view` once. The AI Gateway meters `ai.assistant.monthly_requests` through it in place of counting call records.
- **Expiry (slice 3, T013).** `subscription.contract_ends_at null`, set by the platform with the subscription. After it, the tenant is in grace for `expiry_grace_days` (default 30), with a banner for administrators; after grace the tenant is read only: `IEntitlementService.StateAsync` returns `active`, `grace` or `read_only`, and a request filter refuses writes (`POST`, `PUT`, `PATCH`, `DELETE`) of tenant users with `entitlement.expired`, except sign-in, sign-out and the platform API. Reads keep working so data can be exported.
- **Plan and usage page (slice 4, T014 extended).** `/admin/plan` adds the add-ons and overrides with their end dates, a usage bar per metered quota for this period, the renewal or contract end date and the grace or read-only state. No platform console exists in the product, so add-ons, overrides, enforcement and contract dates stay platform API operations (smallest safe default; a platform UI is not in any approved spec).
- Tests: precedence and validity windows, override reverting on its end date, another tenant's add-on never applying, tenant admin refused on every platform route, concurrent consumption within the tolerance, grace mode, exhaustion notified once per period, release, expiry states and the write refusal, audit entries.

