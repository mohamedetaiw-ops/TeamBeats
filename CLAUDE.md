# CLAUDE.md

You are contributing to a spec-driven enterprise product (TeamBeats, working name: an AI-native enterprise intranet). Approved specifications and the constitution are the product authority. Your job is to implement approved behavior, keep docs and tests in sync, and preserve architectural constraints.

## Read first, every task
1. `.specify/memory/constitution.md`
2. The affected spec(s) in `specs/NNN-*/spec.md`, and their `plan.md` and `tasks.md` if present
3. Relevant ADRs in `docs/architecture/decisions/`
4. `docs/design-system/` for any user interface work (binding, constitution XXXI)
5. `docs/product/foundation-v0.1.md` for background (lower authority than approved specs)

## Workflow
- New or changed behavior: spec first (`/specify`), resolve `[NEEDS CLARIFICATION]` (`/clarify`), then `/plan`, `/tasks`, then implement.
- Only implement specs with status **Approved**. Proposed specs are not implementation authority.
- When a spec is silent, do not invent a product rule. Record the gap and propose the smallest safe default.
- Do not change a spec to match code. Do not change an architecture decision without a proposed ADR.

## Hard rules
1. Preserve `tenant_id` and organization scope through every API, query, job, object path, cache key, search document and AI request. Tenant comes from the session, never from client input.
2. Never rely on UI hiding for authorization. Use the central authorization service; never check role names.
3. Check package features through the entitlement service with capability keys; never check package names.
4. All model calls go through the AI Gateway. All external systems go through connector adapters.
5. Do not query another module's tables. Use its contracts or events.
6. No new frameworks, libraries, services or infrastructure without explaining why the current stack is insufficient. No commercial or non-OSI licensed dependency without an ADR.
7. Write tests for all changed behavior, including cross-tenant denial, org boundary, entitlement and failure cases. Never skip or disable a test to get green.
8. No user-facing literal strings; use resource keys. Layouts must work RTL.
9. Never put secrets, tokens or real customer data in code, prompts, logs, fixtures or commits.
10. Inside a module, keep domain code free of infrastructure: use cases reach databases, messaging, other modules and external systems only through ports (ADR-0013). Name modules as in `docs/architecture/module-map.md`.
11. Keep the frontend thin: no business rules, role or package checks in React code; components never import each other and talk only through declared events; client caches are keyed by tenant and scope (constitution XXX, ADR-0014).
12. Tenant-configurable behavior is versioned, schema-validated configuration, never code per customer (constitution XXIX).
13. Build UI only from the design system: shared components, `--tb-*` tokens, the icon set, images with alt text; pass `npm run verify` and the design suite in Beats, Atlas, Pulse and Studio (constitution XXXI, spec 036).

## Report at the end of each task
- **Specification:** IDs and files reviewed and changed
- **Architecture impact:** modules, data model, API, integration, security
- **Implementation:** short summary of code changes
- **Tests:** suites run and results, including tenant-boundary tests
- **Documentation:** specs, ADRs, OpenAPI updated
- **Outstanding:** open decisions, risks, deviations
