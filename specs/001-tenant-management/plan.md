# Implementation Plan: Tenant Management

**Spec:** `specs/001-tenant-management/spec.md` (v1.0, Approved) | **Branch:** `001-tenant-management` | **Date:** 2026-11-04
**Shared base:** `docs/architecture/r0-solution-structure.md`

## Summary
Build the tenant registry, tenant lifecycle (Provisioning, Active, Suspended, Closed), tenant settings and branding, and the platform-admin tenant creation flow inside the `Tenancy` module. Creation is one transaction that also seeds default roles (via the Authorization contract), assigns a package (via the Entitlements contract), and creates pending admin memberships.

## Technical context
| Item | Value |
|---|---|
| Modules touched | Tenancy (owner), Authorization, Entitlements, Audit (through contracts) |
| New dependencies | none beyond the shared base |
| Storage | `tenancy` schema; branding assets in object storage at `tenants/{tenant_id}/branding/` |

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I Tenant boundary | Yes | `tenant_settings`, `branding_asset` have tenant_id + RLS. `tenant` is a documented RLS exception (platform registry), only reachable from platform admin endpoints |
| III Central authorization | Yes | Platform operator and tenant admin checks through `IAuthorizationService` |
| VI Audit | Yes | Events from spec section 13 via `IAuditWriter` in the same transaction |
| XIV Module boundaries | Yes | Roles and packages created through contracts, not tables |
| XXI Entitlements | Yes | Package assignment through `IEntitlementService` |
| XXIII Localization | Yes | Locale lists validated against supported set; all errors as resource keys |
| Others | n/a or Yes | No AI, no connectors |

## Data model (schema `tenancy`)
- `tenant` (platform): `id uuid pk`, `key citext unique`, `display_name`, `status`, `created_at`, `closed_at`, `deletion_due_at`. RLS exception.
- `tenant_settings`: `tenant_id pk/fk`, `default_locale`, `enabled_locales text[]`, `time_zone`, `primary_color`, `logo_asset_id`, `host_name` (subdomain or dedicated host), `row_version`.
- `tenant_admin_appointment`: `tenant_id`, `id`, `invited_email`, `user_id null`, `status (pending, active, revoked)`.
- Reserved keys in configuration, checked in the domain.

## API (v1)
| Method | Path | Who |
|---|---|---|
| POST | `/api/v1/platform/tenants` | Platform operator |
| GET | `/api/v1/platform/tenants?status=&q=` | Platform operator |
| POST | `/api/v1/platform/tenants/{id}:suspend` / `:reactivate` / `:close` | Platform operator |
| GET/PUT | `/api/v1/tenant/settings` | Tenant admin (tenant from session, no ID in path) |
| PUT | `/api/v1/tenant/branding/logo` | Tenant admin |
| POST | `/api/v1/tenant/admins` | Tenant admin |

Creation accepts an `Idempotency-Key` header.

## Key behaviors
- Suspension: sets status, publishes `TenantSuspended`; Identity module revokes sessions on that event; workers check tenant status before processing (shared consumer filter).
- Close: status Closed, `deletion_due_at = now + 90 days` (contract override field). Deletion job itself is out of scope.
- Logo: PNG, JPEG, SVG up to 1 MB; SVG sanitized server-side (strip scripts, event handlers, external references) or rejected if sanitization fails.
- Concurrency on key: unique index on `citext key`; the losing transaction returns 409.

## Tests
- Integration: create tenant happy path; duplicate key (case-insensitive); bad package rolls back everything; reserved key; concurrent create.
- Security: tenant admin of A cannot read/update B settings (no ID in path, but test tampering through headers and host); tenant admin cannot call platform endpoints; SVG with script rejected.
- Suspension: sign-in refused, session revoked, worker skips message.
- Audit assertions for every event in spec section 13.

## Risks
- Host-based tenant resolution must match spec 003 discovery. Shared helper in `TenantContext`.
