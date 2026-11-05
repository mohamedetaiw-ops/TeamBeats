# Architecture patterns recommendation: review against the constitution

Date: 2026-11-05
Reviewed for: Mohamed
Input: the "Modular Monolith + Hexagonal + DDD backend, Component-Driven + Schema-Driven UI + MVVM frontend" recommendation shared in the project on 2026-11-05 (sections 1 to 8: patterns, target architecture, frontend patterns, component contract, behavioral principles, AI patterns, things to avoid, technology alignment, day-one rules).

## 1. Verdict

The recommendation agrees with the constitution on almost every point. Most of it is already law in TeamBeats (modular monolith, AI Gateway, adapters, schema-driven pages, tenant context everywhere, no customer forks). Where it adds something real, it adds rules the constitution only implied: layering inside a module, explicit bounded contexts, attribute conditions on permissions, a thin frontend, and versioned configuration and extension contracts. Those are adopted as constitution amendments and new ADRs.

Two technology choices conflict with ratified decisions and are rejected: NestJS as the backend and Temporal as the workflow runtime. Neither rejection costs any of the patterns the recommendation asks for, because every pattern it lists is language neutral and already has a home in the .NET stack.

## 2. Item by item

Legend: **Already in place** (no change), **Adopt** (new or sharper rule), **Adapt** (adopt with a constraint), **Reject** (conflicts with the constitution or an accepted ADR).

### 2.1 Architecture patterns (section 1)

| Recommendation | Decision | Reasoning against the constitution | Where it lands |
|---|---|---|---|
| Modular monolith | Already in place | Constitution XIV, ADR-0001. | No change |
| Hexagonal / Clean architecture in the backend | Adopt | XIV fixes boundaries between modules but says nothing about structure inside one. R0 modules are flat folders, which is fine for CRUD but will not hold for Workflow, Agents or Experience. Adopt ports and adapters inside each module, kept light: no generic repositories, no mediator, no mapping library (r0-solution-structure section 5 still applies). | Constitution XIV, ADR-0013 |
| DDD with bounded contexts | Adopt | The modules already behave like bounded contexts, but there is no single list of them, their owned data or their events. Without one, each spec names modules its own way. | Constitution XIV, [module map](../architecture/module-map.md), ADR-0013 |
| Component-driven frontend | Already in place | Spec 032 FR-024 to FR-035, ADR-0012. | No change |
| Schema-driven UI | Already in place | ADR-0012 decision 2 and 3; builder forms generated from manifest schemas (032 FR-025). | No change |
| MVVM + unidirectional data flow | Adapt | Useful as a rule ("view components render, hooks hold view state, business rules stay on the server"), not as a framework. React server components already do most of the work. | Constitution XXX, ADR-0014 |
| Strategy, State, Observer for component behavior | Adapt | Implementation guidance, not constitution material. Recorded in ADR-0014 with the places they apply. | ADR-0014 |
| Ports and adapters for integrations | Already in place | Constitution XVII, connector framework (planned 017). | Constitution XVII gains a degraded-mode rule |
| Plugin architecture + event-driven agents | Already in place | Tool registry and tool gateway (027 FR-005, FR-006), triggers (020), ADR-0011. | No change |
| Metadata-driven configuration | Adopt | Constitution XX bans customer forks but does not require configuration to be versioned, schema-validated and bounded. | New constitution XXIX |
| Event-driven cross-module communication | Already in place | ADR-0001 (outbox), ADR-0006. Event schema versioning is added. | Constitution XXIX, module map section 3 |
| RBAC + ABAC | Adapt | Spec 004 is RBAC with scopes and resource grants and no conditions. Adding attribute conditions is valuable (contractors, job grade, location, classification), but a free policy language would break explainability (004 FR-010) and the "union of grants" model. Adopt conditions over a fixed attribute catalogue that can only narrow a grant. | Constitution III, spec 004 FR-014 to FR-016 |

### 2.2 Target architecture (section 2)

| Element | Decision | Reasoning |
|---|---|---|
| Experience layer (web, mobile, workspaces, themes, RTL) | Already in place | Specs 007, 008, 032, 033; mobile is planned 030. |
| API gateway / BFF | Adapt | The BFF already exists: the Next.js server and the API share one origin per tenant host (r0-solution-structure section 5). A separate API gateway product is new infrastructure without measured need (XV), so it is not added. |
| Page schema renderer, personalization, tenant context | Already in place | ADR-0012. |
| Modular core list | Already in place | Every module listed maps to a spec; the module map now names them all. |
| PostgreSQL, Redis, object storage, event bus | Adapt | Redis stays optional (foundation section 7, ADR-0007 chose PostgreSQL sessions so no install needs Redis). No correctness may depend on Redis. |
| Entra ID, M365, SAP, Oracle, OpenRouter | Already in place | Specs 003, 013, planned 017 to 019. |

### 2.3 Frontend patterns and component contract (sections 3 and the example)

| Pattern | Decision | Where it applies |
|---|---|---|
| Composite | Already in place | Page, layout, region, section, placement tree (032). |
| Strategy | Adopt as guidance | Layout and navigation variants, density, presentation modes (ADR-0014). |
| Factory / Registry | Already in place | Component registry resolved by manifest reference (ADR-0012 decision 3). |
| Observer / Pub-Sub between widgets | Adopt | Not yet specified. Placements may only talk through events declared in their manifest, over a page-scoped event channel. | Spec 032 FR-024, FR-067, FR-068 |
| State machine | Adopt as guidance | Page and template lifecycle (032 FR-021), builder save states, agent runs (ADR-0011). |
| Command (add, move, undo, redo) | Adopt as guidance | Builder undo and redo (032 FR-043) and personal layout undo (008 FR-027) are implemented as a command stack. |
| Adapter | Already in place | Data access through module contracts (032 FR-027). |
| Decorator | Adapt | The renderer wraps each placement (error boundary, telemetry, visibility, accessibility). A decorator that hides a placement is targeting, never authorization (constitution III). |
| Template method | Adopt as guidance | Shared placement lifecycle (load, ready, error, empty) from the manifest error and lifecycle modes. |
| `ComponentDefinition` interface | Already in place, two gaps closed | 032 FR-024 already has identity, version, schema, capabilities, defaults. It lacked declared events, actions and a data contract; FR-024 now includes them. |

### 2.4 Behavioral principles (section 4)

| Principle | Decision | Where it lands |
|---|---|---|
| Configuration over customization | Already in place, sharpened | XX plus new XXIX (versioned, validated, bounded configuration). |
| Composition over inheritance | Already in place | 032 FR-014, ADR-0012. |
| Event-driven interactions | Adopt | XXX (no component imports another), 032 FR-067. |
| Policy-driven behavior | Already in place, sharpened | III (central decisions, now with conditions), XXI (entitlements), 013 (AI policy), 008 (personalization policy). |
| Progressive enhancement | Adopt | XII covers AI. XXX extends it to optional components and XVII to connectors. |

### 2.5 AI patterns (section 5)

All six are already in the specs. No change.

| Pattern | Home |
|---|---|
| AI Gateway, agent registry, tool adapters, approvals, policies, audit, metering | Constitution VIII to X, specs 013, 020, 027 |
| Strategy for model and provider selection | 013 routing by classification and policy |
| Tool registry / plugin | 027 FR-005, FR-006 |
| State machine for agent execution | ADR-0011 run loop with checkpoints |
| Human in the loop | Constitution X, 027 approval matrix |
| Event-driven triggers | 020 US8 and FR-014 (agent-prepared tasks, proactive agents) |
| Saga / workflow orchestration | ADR-0010 process engine, ADR-0011 L4 agent tasks inside BPMN |
| OpenRouter behind an abstraction, local models for sovereign installs | Constitution VIII, 013 private OpenAI-compatible endpoints |

### 2.6 What to avoid (section 6)

Every row agrees with the constitution. "Business logic inside React components" and "large global frontend state" were not written down anywhere and are now in XXX. "Unlimited admin customization" is now in XXIX.

### 2.7 Technology alignment (section 7)

| Layer | Recommended | Decision | Reasoning |
|---|---|---|---|
| Frontend | React, TypeScript, Next.js | Already in place | Next.js 16, TypeScript strict. |
| Design system | Tailwind CSS + Radix UI | Adapt | Radix primitives (MIT) give accessible, RTL-aware behavior for menus, dialogs, tabs and popovers, which supports XXIV and saves work. Tailwind (MIT) is allowed only if its theme maps to the semantic tokens of spec 033, arbitrary values are banned, and physical direction utilities (`ml-`, `pl-`, `left-`) are banned by lint, because XXIII requires logical properties and 032 FR-026 requires tokens only. ADR-0014. |
| Page engine | JSON Schema renderer with registry | Already in place | ADR-0012. |
| Frontend state | TanStack Query + Zustand | Adapt | TanStack Query (MIT) for client-side server state on interactive surfaces (builder, task inbox, notifications), with tenant and scope in every query key (032 FR-065). Zustand (MIT) only inside feature stores that need it (builder document, personalization edit mode); no app-wide store of business data. ADR-0014. |
| Backend | NestJS with TypeScript | **Reject** | Constitution XIV names ASP.NET Core; ADR-0001, 0006, 0009, 0010 and 0011 are built on .NET; R0 is already written in .NET 10 with 169 passing tests. A second backend language would split tenancy, authorization, audit and entitlement code in two, which is exactly what ADR-0011 avoided for agents. Every backend pattern recommended (hexagonal, DDD, events, plugins) works in .NET. |
| Database | PostgreSQL | Already in place | ADR-0002. |
| Cache | Redis | Adapt | Optional only (see 2.2). |
| Event messaging | RabbitMQ initially | Already in place | ADR-0006. |
| Workflows | Temporal | **Reject for now** | Temporal is MIT, so the licence is fine, but it adds a cluster and its own database to every install including air-gapped ones (XV, XX), keeps tenant state outside PostgreSQL RLS (I), and models processes as code while specs 016 and 024 require BPMN and DMN that business owners can read. The recommendation itself says Temporal should come only when complexity justifies it. ADR-0010 now lists it as a considered option with a revisit trigger. |
| AI integration | Provider-neutral gateway, OpenRouter first | Already in place | ADR-0003, spec 013. |
| Search | PostgreSQL full text and pgvector first | Already in place | Constitution XV, foundation section 7. |
| Deployment | Containers, Kubernetes compatible | Already in place | Foundation section 9, r0-solution-structure `/deploy/helm`. |

### 2.8 Day-one rules (section 8)

| Rule | Status |
|---|---|
| Tenant and org context in every request, query, event and job | Tenant part already in I. Org part added to II (events and jobs about organization-owned data carry the org unit). |
| Versioned page definitions, component configs, themes, templates | 032 and 033 already. Generalized to all configuration in XXIX. |
| Same component across pages, orgs and tenants | 032 FR-014 and the swap test. |
| Components never touch databases or bypass authorization | 032 FR-027, now also XXX. |
| Branding and configuration without code forks | XX, 033. |
| Backward-compatible versioning of extensibility contracts | New XXIX. |
| Agents under scoped identities, tool permissions, auditable execution | X, 027. |
| Module boundaries enforced by structure and architecture tests | XIV, extended to layering inside modules and to the frontend (XXX). |

## 3. Constitution amendments (need Mohamed's approval)

Proposed version 1.1.0 (MINOR: two principles added, three expanded, none removed or redefined). Merging the pull request that carries them is the approval, as with earlier ADRs.

1. **II Organization scope** expanded: events, jobs and cache keys about organization-owned data carry the org unit ID.
2. **III Central authorization** expanded: roles and scopes may carry conditions over a fixed attribute catalogue, conditions only narrow, policies are data, never tenant code.
3. **XIV Modular monolith** expanded: ports and adapters inside each module with dependencies pointing inward; the module map is the list of bounded contexts.
4. **XVII Integration adapters** expanded: product features degrade gracefully when a connector is down.
5. **New XXIX Configuration over code, under versioned contracts.**
6. **New XXX Thin, composable frontend.**

New principles are numbered XXIX and XXX in a new Part F so that no existing principle number changes; every spec that cites a principle by number stays correct.

## 4. Other changes in this pass

- ADR-0013 Module internal architecture and bounded contexts (new).
- ADR-0014 Frontend application architecture (new).
- ADR-0010 gains Temporal as a considered option with a revisit trigger.
- Module map (new): `docs/architecture/module-map.md`.
- Spec 004: attribute conditions (FR-014 to FR-016, User Story 7), R1.
- Spec 032: manifest declares events, actions and data contract (FR-024); placement events and component isolation (FR-067, FR-068).
- `r0-solution-structure.md`: module layering and frontend libraries.
- Plan template: Constitution Check rows for XXIX and XXX; frontend libraries in technical context.
- `CLAUDE.md`: two hard rules for the frontend and module layering.

## 5. What the recommendation asks for next, and status

It names an ADR pack, module boundaries, component contracts, page schema specs, behavior contracts and enforcement rules as the next deliverables. After this pass: the ADR pack is 0001 to 0014; module boundaries are in the module map and ADR-0013; component contracts and page schemas are spec 032; behavior contracts are 032 FR-067 and FR-068 and ADR-0014; enforcement rules are the verification lines of XIV, XXIX and XXX. The page schema itself (the JSON Schema files for page, layout, template and manifest) is the remaining artifact and belongs in the 032 plan, written when 032 is approved.
