# Implementation Plan: Security Controls, Compliance and Certifications

**Spec:** `specs/026-security-and-compliance/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** `docs/architecture/r0-solution-structure.md`, ADR-0005 (tenant-scoped identity), ADR-0007 (session store), ADR-0013, spec 003 plan (sessions and Entra sign-in), spec 004 plan (attribute conditions)

## Summary
R1 builds the tenant security policy and the basic alerts: User Story 1 (network rules), User Story 2 (MFA assurance, step-up, session rules), User Story 5 scenarios 1, 2 and 4 within the package split of section 9, the security posture page (FR-017), and secure rendering checks (FR-025). Guests, SIEM, data detection, compliance projects, customer keys and four-eyes are R2 or R3 and not planned here. The certification roadmap is a company program; R1 adds only the product evidence it needs (the trust documents list in the admin shell links to files the platform operator uploads).

R1 slices, in order:
1. **Security policy store and enforcement:** IP rules and session rules, enforced in the request pipeline before any module code.
2. **MFA assurance and step-up:** claim mapping per identity provider, refusal without MFA, step-up flow and the sensitive action list.
3. **Alerts and posture:** download volume and out-of-hours admin grants, alert handling, posture page.
4. **Admin pages:** Security policy, Alerts, Posture, in English and Arabic, RTL.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation | Yes | Policy and alerts tenant-owned with forced RLS; enforcement reads the policy of the tenant resolved from the host and session |
| III authorization | Yes | `security.manage`, `security.view`, `security.respond` through `IAuthorizationService`; per-role network rules are spec 004 conditions, so the explainer shows them |
| V identity | Yes | No product-held credentials; MFA stays with the identity provider; step-up is a fresh OIDC sign-in with `max_age` and `prompt=login` |
| XXI entitlements | Yes | Section 9 capability keys checked through `IEntitlementService` |
| Rule 9 secrets | Yes | Audit records before and after values, never secrets |

## Module
The `Security` module of the module map, with `Security.Contracts`. Session enforcement stays in `Identity` (it owns sessions); it reads the policy through `ISecurityPolicy` from `Security.Contracts`.

## Data model (schema `security`)
- `security_policy`: one row per tenant, versioned: `ip_allow cidr[]`, `ip_block cidr[]`, `admin_ip_allow cidr[]`, `network_zones jsonb` (name to CIDR list), `require_mfa`, `step_up_minutes` (default 10), `idle_minutes` (30), `absolute_hours` (12), `max_sessions` (null for no limit), `remember_device_days`, `alert_settings jsonb`, `version`, timestamps.
- `mfa_claim_mapping` per identity provider config: which `amr` or `acr` values count as MFA (defaults for Entra ID: `amr` contains `mfa`, or `acr` names a multi-factor policy).
- `security_alert`: `tenant_id`, `id`, `kind`, `subject_user_id`, `facts jsonb` (counts and references, no content), `status` (open, handled, dismissed), `handled_by`, `note`, timestamps.
- `identity.user_session` gains `last_seen_at` and `authenticated_at` (spec 003 table, migration in `Identity`).

## Enforcement
- **Client address:** only `RemoteIpAddress` after the forwarded headers middleware, which already trusts only configured proxies (`Http:TrustForwardedHeaders`, FR-002). Client-set headers are never read.
- **IP rules (US1):** a middleware right after tenant resolution: blocklist first, then tenant allowlist, then the admin allowlist for `/api/v1/admin`, `/api/v1/authz`, `/api/v1/security` and the other admin route groups, marked by an endpoint metadata `AdminArea`. Refusal is 403 `security.network_not_allowed` with no session created; audit `security.network_refused` with the source address, rate-limited per address to avoid audit floods.
- **Network zones (US1 scenario 5):** a new condition attribute `network_zone` for spec 004 conditions, resolved per request from the zones in the policy.
- **Self lock-out (US1 scenario 3):** saving rules that exclude the caller's address needs step-up and a typed confirmation field; break-glass (spec 001) is the recovery.
- **Session rules (US2 scenario 4):** the session middleware ends a session idle longer than `idle_minutes` or older than `absolute_hours`; `last_seen_at` is written at most once a minute. Concurrent limit: the oldest session is revoked on a new sign-in. `POST /api/v1/me/sessions/revoke-all` and the admin action for a user revoke all sessions (`SessionRevoked`), within 1 minute.
- **MFA assurance (US2 scenario 1):** at Entra sign-in the ID token's `amr` and `acr` are checked with the mapping; when `require_mfa` is on and MFA is not shown, no session is created and `identity.sign_in_refused` is audited with reason `mfa_missing`. The default rule of section 6 applies: `require_mfa` turns on only after a test sign-in shows the claim.
- **Step-up (US2 scenarios 2 and 3):** sensitive endpoints carry `RequireStepUp` metadata. When `now - authenticated_at > step_up_minutes`, the API answers 401 `security.step_up_required` with a step-up URL; the web shell sends the user to the identity provider with `prompt=login` and `max_age=0`, and the callback refreshes `authenticated_at` only if `auth_time` is fresh. The default list of section 6 is mapped to existing endpoints (role assignment, identity configuration, security policy, audit export, connection credentials); R2 items attach as they are built. Dev sign-in (dev host only) is treated as fresh.

## Alerts (US5, R1 part)
- Download volume: the `Documents` module (spec 011) publishes `DocumentDownloaded` (IDs only); `Security` counts per user per hour and raises `download_volume` above the tenant threshold (default 200 files or 2 GB). Built when spec 011 downloads exist; until then the rule is wired and tested with the event.
- Out-of-hours admin grant (`security.alerts_advanced`): on `RoleAssignmentChanged` for a role holding any admin permission, outside the tenant working week and hours (tenant default calendar of spec 022, hours 08:00 to 18:00 by default), raise `privilege_grant_out_of_hours`.
- Blocked network sign-ins: counted from the network refusal path.
- Handling: `POST /api/v1/security/alerts/{id}/handle {outcome, note}` audited as `security.alert_handled`. Notification to security officers through spec 015 when it exists; until then the alert list and an admin shell badge.

## Posture (FR-017)
`GET /api/v1/security/posture` lists checks with state and a link: MFA required, IP rules set, idle and absolute timeouts at or below the recommended values, admin area rule set, step-up window at or below 10 minutes, Entra sign-in configured, dev sign-in absent (production artifact). No score formula in the spec, so the page shows passed checks out of total.

## Secure rendering (FR-025)
Already partly built for pages (spec 032 FR-035). R1 adds an architecture or e2e test that the web app's content security policy has no `unsafe-inline` for scripts, and a test that rich text fields go through the server sanitizer.

## API (v1)
- `GET/PUT /api/v1/security/policy` (`If-Match`, step-up), `GET /api/v1/security/policy/preview?ipAllow=` (users who signed in from outside the range in the last 30 days, a count).
- `GET /api/v1/security/alerts`, `POST /api/v1/security/alerts/{id}/handle`.
- `GET /api/v1/security/posture`.
- `POST /api/v1/me/sessions/revoke-all`, `POST /api/v1/identity/users/{id}/sessions/revoke-all`.

## Tests
- Integration: US1 scenarios 1 to 5 (allowed, blocked, admin area only, self lock-out confirmation, forwarded header spoofing ignored, network zone condition with explainer), US2 scenarios 1 to 4 (MFA claim present and missing, step-up required and satisfied, idle and absolute timeout with a fake clock, revoke all), US5 scenarios 1, 2 and 4.
- Cross-tenant: tenant A's policy never applies to tenant B; alerts of A not visible in B; policy endpoints reject cross-tenant IDs.
- Entitlements: scoped IP rules and advanced alerts refused on Workplace.
- Audit: every policy change with before and after values; no secrets.
- Acceptance (e2e): edit policy with step-up and preview, handle an alert, posture page, in English and Arabic with axe.
