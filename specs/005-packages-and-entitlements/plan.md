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
