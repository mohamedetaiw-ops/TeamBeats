# Review: AI Intranet Product Foundation v0.1

Reviewer: Claude, for Mohamed
Date: 2026-11-04
Document reviewed: `AI_Intranet_Product_Foundation.md` (Foundation v0.1), cross-checked against the .docx

## Source used

Mohamed supplied the foundation twice: as a Word document and as Markdown (`AI_Intranet_Product_Foundation.md`). The Markdown is the source of truth for this pack and is stored at `docs/product/foundation-v0.1.md`.

Differences between the two:

- **Text:** none. A word-by-word comparison of the two versions found identical text, tables and requirement IDs.
- **Diagrams:** the Markdown links three images (`assets/architecture.png`, `assets/tenant-model.png`, `assets/spec-lifecycle.png`) that were not included in the upload. I extracted the same three diagrams from the Word file and saved them under `docs/product/assets/` so the Markdown renders correctly.
- **One diagram detail worth noting:** the logical architecture diagram shows "Mobile Clients, future native apps" as a box in the experience layer, while open decision 8 still asks whether a native app is needed. Treat the diagram box as future scope, not a commitment.

## Overall verdict

The foundation is a strong architecture baseline. It makes clear defaults (modular monolith, shared schema with RLS, AI Gateway, federation first), it is honest about what needs business validation, and it already contains a usable constitution, spec quality standard and agent operating rules. Very few intranet product documents get tenant isolation and permission-aware AI this right on the first draft.

Its main weakness is that it is much stronger as an architecture document than as a product document. It explains how to build the platform safely, but it does not yet say what the first sellable release is, who it is for in concrete terms, or how success will be measured. Almost every requirement is marked "Must for first production release", which makes the first release the whole product.

The findings below are grouped by severity. "Blocker" means it should be resolved before the related spec is approved. "Important" means it should be resolved during Phase 0 or 1. "Minor" means fix when convenient.

## Blockers

### B1. There is no MVP cut, and "M" means almost everything

57 of the 71 requirement IDs are marked M, meaning "must for first production release" (56, plus IAM-001 as M for development only). That includes the Microsoft Graph integration, HR sync, workflows with delegation and escalation, the agent and tool registry, and five fully localized languages. Meanwhile the roadmap spreads these over six phases. Those two views conflict.

Recommendation: define release slices explicitly, for example:

| Release | Contents | Purpose |
|---|---|---|
| R0 Internal alpha | Phase 1 foundation, dev auth, tenant and org model, admin shell | Prove isolation and the engineering loop |
| R1 Pilot | Entra ID, home, news, pages, directory, document library, search, cited AI assistant, Arabic and English | First design-partner customer |
| R2 GA | Forms and workflows, M365/SharePoint, HR sync, entitlements and metering, remaining languages | First paid packages |
| R3+ | Agents with write tools, Oracle Fusion, SAP, on-prem hardening | Enterprise & Government |

Then replace the M/S/L labels with the release in which each requirement is due. Each spec should name its target release.

### B2. Cross-tenant identity changes the data model now

Open decision 6 (can one person belong to several tenants) is listed as a business question, but section 6.2 already models `UserIdentity` as a global table with no `tenant_id`. In a shared installation, a global identity table is a cross-tenant data surface: it holds names and emails from every customer and is outside RLS.

Recommendation: decide this in Phase 0, not later. My suggested default is that identities are tenant-scoped. A user is identified by (tenant, identity provider configuration, subject). The same human in two tenants is two unrelated accounts. Cross-tenant identity can be added later as an explicit, audited link. This is reflected in spec 003 as a clarification item.

### B3. Entitlements arrive too late in the roadmap

Package entitlements and metering are in Phase 6, but AI quotas (AI-006), package-gated features (section 4) and UC-12 affect every module. If modules are built without a capability check, gating will be retrofitted across the whole codebase.

Recommendation: build the entitlement service skeleton in Phase 1 (capability keys, a `IsEnabled` / `CheckQuota` contract, a default package seeded for every tenant), and leave pricing, add-ons and billing exports for later. Spec 005 is written for this.

### B4. No success measures

There are no product KPIs, adoption goals or business outcomes. Without them, specs cannot have measurable success criteria and the team cannot judge whether a release worked.

Recommendation: add a short "Outcomes and measures" section to the product vision, for example weekly active employees as a share of licensed users, search success rate (search followed by a click with no re-query), AI answer helpfulness rating and citation click-through, time to publish targeted news, and service request cycle time. Each spec in this pack has a Success Criteria section that will reference these.

## Important

### I1. Personas and competitive position are missing

The user groups are listed as roles, but there are no personas with goals and pain points, and no positioning against Microsoft Viva Connections and SharePoint, Staffbase, LumApps, Unily or Simpplr. The differentiators in the doc (on-prem, Arabic and RTL first, organization-aware multi-entity tenants, AI in every tier) are real, but they should be stated as the reason to buy, because they drive priorities. For example, if on-prem government is the wedge, on-prem hardening cannot wait until Phase 5.

### I2. Data residency will probably force a second AI path early

The launch language set (Arabic, Urdu, Hindi, English, French) suggests a Gulf, South Asia and North Africa market. That is my inference, so please confirm. Government buyers in that region commonly require in-country processing. OpenRouter as the only provider means sensitive workloads (Confidential, Restricted) are blocked by default, which may leave government tenants with very little usable AI.

Recommendation: keep OpenRouter first, but spec the provider adapter contract now (AI-011) and plan a second adapter (an in-region cloud model endpoint or a local inference server) for the first government pilot. Treat open decisions 3 and 9 as Phase 0 blockers for the Enterprise & Government package.

### I3. Build vs buy is not decided for the hardest modules

The doc decides the platform stack but not the three largest functional components:

- Workflow engine and form builder (build on a .NET workflow library, or build a small state machine).
- Page and content editor (block model, rich text editor, page layout and widgets).
- Search ranking for Arabic and Urdu.

Each needs an ADR before its spec is approved. They are the biggest schedule risks in Phase 2 and 4.

### I4. Library licensing risk in the .NET ecosystem

Several popular .NET libraries moved to commercial licences in 2025 (for example MediatR, AutoMapper and MassTransit v9). The constitution should require an ADR or explicit approval before adding any dependency with a commercial or non-OSI licence, because this product is sold and deployed on-prem. Added as a constitution rule.

### I5. Dev-only authentication versus "same code line"

IAM-003 says the local password provider must be removed from the production code line, while C-20 says all deployments use the same code line. Both can hold if the dev provider lives in a separate assembly that only the development host references, plus a startup guard and a CI test. That should be stated as the mechanism so it is not interpreted as "delete it before beta and lose dev tooling". Spec 003 does this.

### I6. Targeting is not the same as authorization

The doc mixes audience targeting ("show this news to subsidiary X") with access control ("only subsidiary X may read it"). They behave differently: a targeted item can still be readable by others through search or a link, while a restricted item cannot. Specs must keep them separate. Spec 004 defines the difference.

### I7. RLS details that need to be explicit

The RLS section is good. Three things need to be specified to avoid common failures:

- Tenant context set with `set_config(..., is_local => true)` inside every transaction, including reads, with a fail-closed policy when the setting is missing (no tenant means no rows, never all rows).
- Platform operator and migration roles are separate database roles from the application role, and cross-tenant operator queries go through a dedicated, audited path.
- Background workers carry the tenant in the message and set context per message, never per worker process.

### I8. Scale assumptions are needed for design, not only for sizing

Open decision 7 is marked as later, but the HNSW versus partitioned vector decision, the closure table approach, and the audit table design all depend on rough numbers. Recommendation: agree "design-to" targets now (for example up to 50 tenants per shared installation, 100,000 users per tenant, 2 million indexed chunks per tenant) and revise later.

### I9. Employee analytics and privacy

Engagement analytics on individuals can conflict with GDPR (French market), works councils and local privacy law. Analytics specs should default to aggregated reporting with minimum group sizes, and per-user analytics should be a tenant policy decision.

### I10. AI cost in the entry package

"AI in every package" with central OpenRouter billing means the entry package carries variable model cost. The package design needs a cost model per active user, with default quotas and a cheap default model for the entry tier. This belongs in the packages spec and the commercial decision list.

## Minor

- **Requirement ID errors.** LOC-005 and LOC-006 are analytics requirements filed under the localization prefix. Rename them ANL-001 and ANL-002.
- **Missing requirement IDs.** There are no IDs for entitlements, notifications, audit retention, personalization, file upload scanning or non-functional requirements. Suggested prefixes: ENT, NOT, AUD, NFR. Spec 005 introduces ENT-xxx and spec 006 introduces AUD-xxx.
- **Tamper-resistant audit is undefined.** State the mechanism: append-only table, no UPDATE or DELETE grant for the application role, and optionally a hash chain per tenant.
- **Approvers are not named.** The spec status model needs a named approver per area (product, architecture, security). For now that is Mohamed for all.
- **Tenant deletion and backups.** UC-14 should state how deleted tenant data ages out of backups.
- **Notifications** appear in many flows but have no module, spec or requirement IDs.
- **Product name** is still "to be confirmed". The project name TeamBeats is used as a working name in this pack.
- **Repository layout.** Section 19 proposes `specs/<area>/<name>.md`. This pack uses the spec-kit layout (`specs/NNN-feature/spec.md`, then `plan.md` and `tasks.md` next to it) so that standard spec-driven tooling and agent commands work. Each spec records its area and requirement IDs, so nothing from section 19 is lost.

## What I changed or added in this pack

1. `.specify/memory/constitution.md`: the constitution, rewritten from section 17 into principles with rationale and how each is verified, plus new rules for untrusted AI content, dependency licensing, graceful AI degradation, data classification and privacy.
2. `.specify/templates/`: spec, plan and tasks templates that combine spec-kit structure with the doc's section 18 quality standard.
3. `specs/001` to `specs/007`: the Phase 1 foundation specs, all with status Proposed and open questions marked `[NEEDS CLARIFICATION]`.
4. `docs/architecture/decisions/`: the first four ADRs as Proposed.
5. `CLAUDE.md`: agent operating rules from section 20.

## Decisions taken

Mohamed confirmed all four on 2026-11-04:

1. Identities are tenant-scoped, with no global person record (spec 003, B2 closed).
2. Release slices R0 to R3 are adopted (`docs/product/roadmap.md`, B1 closed).
3. The entitlement core is built in Phase 1 (spec 005, B3 closed).
4. R1 launches with Arabic and English complete; French, Urdu and Hindi follow in R2 (spec 007).

Still open: B4 (success measures) and the remaining `[NEEDS CLARIFICATION]` items inside each spec.
