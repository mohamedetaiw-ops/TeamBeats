# Feature Specification: Packages and Entitlements (core)

| Field | Value |
|---|---|
| Spec ID | 005-packages-and-entitlements |
| Area | platform |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R0 (core check and seeded packages), R2 (quotas, add-ons, overrides UI) |
| Last updated | 2026-11-05 |
| Approved | 2026-11-04 by Mohamed |
| Requirement IDs | AI-001, ADM-001 (entitlements part), new ENT-001 to ENT-008 |
| Use cases | UC-12 |
| Depends on | 001, 006 |

## 1. Problem and outcome

**Problem:** The product is sold as three packages from one code line. If features check package names in code, every commercial change becomes a code change and packages drift apart. The foundation roadmap put entitlements in Phase 6, which would mean retrofitting gates into every module (review finding B3). Decided 2026-11-04 (Mohamed): the entitlement core is built in Phase 1 (R0).

**Outcome:** A central entitlement service answers "is capability X enabled for this tenant, and how much quota is left?" using stable capability keys. Every module uses it from the start. Package definitions, add-ons and contract overrides are data, not code.

## 2. Scope

**In scope (R0):**
- Capability catalogue with stable keys, type (feature flag or numeric limit) and unit.
- Versioned package definitions: Workplace, Workplace Plus, Enterprise & Government (working names).
- Tenant subscription: package, version, effective dates, status.
- Backend checks: `IsEnabled(capability)` and `CheckLimit(capability, amount)`.

**In scope (R2):**
- Usage counters and metering per period.
- Add-ons and tenant-specific contract overrides.
- Grace periods and enforcement policy (warn, throttle, deny).
- Admin view of entitlements and usage.

**Out of scope:** pricing, invoicing, payment, CRM sync.

## 3. Actors

| Actor | Can do |
|---|---|
| Platform administrator | Define packages and versions, assign packages, add overrides and add-ons |
| Tenant administrator | View their package, entitlements and usage |
| Any module | Ask for entitlement decisions (system actor) |

## 4. User scenarios

### User Story 1: Feature gating by capability (Priority: P1, R0)

**Acceptance scenarios:**
1. **Given** tenant A on Workplace where `workflow.advanced` is disabled, **When** a user calls a workflow builder API directly, **Then** the backend denies with reason "not included in package" even though the UI hides the button.
2. **Given** tenant B on Workplace Plus where it is enabled, **When** the same call is made by an authorized user, **Then** it proceeds to normal authorization.
3. **Given** a capability key that is not in the catalogue, **When** a module checks it, **Then** the result is "disabled" and an error is logged (fail closed).

### User Story 2: AI in every package (Priority: P1, R0)

**Acceptance scenarios:**
1. **Given** any package definition, **When** it is saved, **Then** validation fails if `ai.assistant` is not enabled. *(AI-001)*
2. **Given** tenant A on the entry package, **When** an employee uses the assistant, **Then** it works within the package's AI quota.

### User Story 3: Quotas (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** `ai.assistant.monthly_requests` limit 10,000 and usage 9,999, **When** a request is made, **Then** it is allowed and usage becomes 10,000.
2. **When** the next request is made and enforcement is "deny", **Then** it is denied with reason "quota exhausted" and the tenant admin is notified once per period.
3. **Given** enforcement is "grace" with 10% grace, **Then** requests continue up to 11,000 with admin warnings.
4. Concurrent requests MUST NOT exceed the hard limit by more than the documented tolerance. (Decided 2026-11-04: tolerance is 1% or 10 units, whichever is larger.)

### User Story 4: Add-ons, overrides and expiry (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** tenant A on Workplace with add-on `integration.sap`, **Then** `integration.sap` is enabled for A only.
2. **Given** a contract override raising `ai.assistant.monthly_requests` to 50,000 until 2027-06-30, **Then** the limit is 50,000 until that date and reverts afterwards.
3. **Given** a subscription whose end date has passed, **Then** the tenant enters the configured expiry state. (Decided 2026-11-04: 30-day grace, then read-only.)

### User Story 5: Package versioning (Priority: P2)

**Acceptance scenarios:**
1. **Given** tenants on Workplace v1, **When** Workplace v2 is published, **Then** existing tenants stay on v1 until moved explicitly.

## 5. Functional requirements

- **ENT-001 / FR-001**: The system MUST maintain a catalogue of capabilities with stable dotted keys (for example `ai.assistant`, `ai.agents.basic`, `workflow.advanced`, `integration.oracle_fusion`, `deployment.on_prem`).
- **ENT-002 / FR-002**: Package definitions MUST be versioned data and MUST include `ai.assistant`. *(AI-001)*
- **ENT-003 / FR-003**: Each tenant MUST have exactly one active subscription with package version and effective dates.
- **ENT-004 / FR-004**: Effective entitlement = package version + active add-ons + active overrides, resolved server-side. Overrides win over add-ons, add-ons over package.
- **ENT-005 / FR-005**: Every gated API, job and AI call MUST check entitlements on the backend. Unknown keys fail closed.
- **ENT-006 / FR-006**: Usage MUST be metered per tenant, capability and period, and quota checks MUST be atomic enough to respect the tolerance.
- **ENT-007 / FR-007**: Denials MUST return a machine-readable reason (not_in_package, quota_exhausted, expired) distinct from authorization denials.
- **ENT-008 / FR-008**: All package, subscription, add-on and override changes MUST be audited.
- **FR-009**: Deployment topology (on-prem, dedicated) is a commercial entitlement, not a code branch.
- **FR-010**: Capability keys MUST NOT reuse a permission key from spec 004 (FR-013). A limit key is named with a `max_` or unit suffix (for example `content.max_sites`, `docs.storage_gb`) so that flags and limits are told apart by name.

## 6. Initial capability catalogue (draft)

| Key | Type | Workplace | Workplace Plus | Enterprise & Gov |
|---|---|---|---|---|
| `ai.assistant` | flag | on | on | on |
| `ai.assistant.monthly_requests` | limit | [TBD] | [TBD] | [TBD] |
| `ai.agents.basic` | flag | on (six launch templates, autonomy L0 to L2) | on | on |
| `ai.agents.department` | flag | off | on | on |
| `ai.agents.advanced` | flag | off | off | on |
| `ai.agents.custom` | flag | off | on | on |
| `ai.agents.max_custom` | limit | 0 | [TBD] | [TBD] |
| `ai.agents.max_published` | limit | [TBD] | [TBD] | [TBD] |
| `ai.agents.marketplace` | flag | on | on | on |
| `ai.agents.orchestration` | flag | off | on (handoffs declared in platform templates) | on (also in customer-built agents) |
| `ai.agents.write_tools` | flag | off | on (autonomy L3) | on |
| `ai.agents.autonomous` | flag | off | off | on (autonomy L4) |
| `ai.agents.mcp` | flag | off | off | on |
| `ai.governance.advanced` | flag | off | on (org unit agent policies, anomaly alerts) | on |
| `ai.governance.security_center` | flag | off | off | on (incidents, watch list, evidence export) |
| `ai.model_choice` | flag | off | on | on |
| `ai.models.private_endpoint` | flag | off | on | on |
| `ai.budgets` | flag | on (tenant budget) | on | on |
| `ai.budgets.scoped` | flag | off | on (org unit, agent and user budgets) | on |
| `org.delegated_admin` | flag | off | on | on |
| `workflow.advanced` | flag | off | on | on |
| `integration.microsoft365` | flag | off | on | on |
| `integration.oracle_fusion` | flag | off | off | on |
| `integration.sap` | flag | off | off | on |
| `analytics.advanced` | flag | off | on | on |
| `analytics.export` | flag | off | off | on |
| `deployment.on_prem` | flag | off | off | on |

Feature specs add their own keys in their section 9: `org.*` (002), `identity.*` (003), `authz.*` (004), `content.*` (009), `experience.*` (008, 032, 033), `docs.*` and `records.*` (011), more `ai.*` keys (013, 020, 027), `workflow.*` (016, 024), more `analytics.*` keys (021), `hr.*` (022), `policy.*` (025), `security.*`, `compliance.*` and `deployment.region_choice` (026). Those keys join this seed data when their spec is approved. `workflow.advanced` in this table is the multi-step workflow key used by spec 016.

Commercial note: values and quota numbers still need commercial confirmation (foundation open decision 1, open items C1 and C2). They are data in package definitions, so they do not block implementation. This table is the seed data for R0.

## 7. Tenant scope
- Subscriptions and usage are tenant-owned. Package definitions are platform-level.
- Cross-tenant: tenant A's add-on never affects B; tenant admins cannot read other tenants' usage.

## 8. Data

| Entity | System of record | Classification |
|---|---|---|
| Capability, package definition | Product (platform) | Internal |
| Subscription, add-on, override | Product (platform, per tenant) | Confidential (commercial) |
| Usage counters | Product | Internal |

## 9. Package entitlements
This spec defines the catalogue itself. It is not gated: the entitlement service runs for every tenant.

## 10. UX
- Tenant admin "Plan and usage" page (R2): package, add-ons, usage bars per quota, renewal date.
- Gated UI shows an informative "not in your plan" state instead of hiding entire navigation, configurable per tenant.

## 11. AI
AI Gateway MUST check `ai.*` entitlements and meter usage before calling any provider.

## 12. Integrations and events
Publishes `EntitlementsChanged` (tenant ID, changed keys) whenever a subscription, add-on, override or package version change alters a tenant's effective entitlements, so that caches, the AI Gateway (spec 013) and gated modules refresh. Publishes `QuotaExhausted` once per tenant, capability and period.

## 13. Audit

| Event | Type |
|---|---|
| ent.package_published | audit |
| ent.subscription_changed | audit |
| ent.addon_changed / override_changed | audit |
| ent.quota_exhausted | audit + notification |

## 14. Security
- Tenant cannot change its own entitlements through any tenant-admin API.
- Entitlement evaluation results are cached per tenant with tenant ID in the key and invalidated on change.

## 15. Non-functional requirements
- `IsEnabled` p95 under 2 ms (cached). Quota check p95 under 10 ms.

## 16. Success criteria
- **SC-001**: A commercial change (enable a feature for a package) requires no code deployment.
- **SC-002**: No feature code references a package name (architecture test).

## 17. Open questions
- Commercial values C1 and C2 (non-blocking, data only).
- AI cost model for the entry package (review finding I10).

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| AI-001 | ENT-002, US2 |
| ADM-001 (entitlements) | US4, section 10 |
| UC-12 | US1 to US4 |
