# Feature Specification: Agent Hub (Registry, Templates, Studio and Marketplace)

| Field | Value |
|---|---|
| Spec ID | 020-agent-hub |
| Area | ai |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (registry, common runtime, six launch templates, basic configuration, lifecycle with review, routing from the assistant, contextual actions), R2 (Agent Studio no-code, customer-created agents, marketplace, evaluation suites in Studio, persistent memory, proactive briefings, department templates), R3 (multi-agent orchestration, advanced templates, extensibility) |
| Last updated | 2026-11-05 |
| Requirement IDs | AI-007 (agent part), AI-008, UC-07, AGT-001 to AGT-020 (new) |
| Use cases | UC-06, UC-07 |
| Depends on | ADR-0011 (agent runtime), 013 (AI Gateway), 014 (assistant and RAG), 027 (agent governance, tools and approvals), 004 (authorization), 005 (entitlements), 006 (audit), 007 (localization), 015 (notifications), 016 (services and task inbox), 017 (connectors), 021 (analytics) |

## 1. Problem and outcome

**Problem:** Customers want AI that does real work for HR, IT, finance and other departments, not one generic chatbot. If each agent is built as its own engine, every new agent needs new code, new integrations and a new security review, and nobody can see how many agents exist, who owns them, or what they can touch.

**Outcome:** TeamBeats has one Agent Hub. Every agent is a versioned configuration run by one common runtime (ADR-0011) and governed by one policy layer (spec 027). The platform ships a library of ready-made agent templates. Tenants install them, adapt them in Agent Studio without code, test them, have them reviewed and publish them. Employees reach agents through the universal assistant, a marketplace, contextual actions on pages and documents, and tasks in their inbox. Administrators always know each agent's owner, version, data access, actions, results and cost.

Three rules apply from day one:
- Every agent is configurable: normal business configuration needs no code change.
- Every action is governed: permissions and approvals are enforced outside the model (spec 027).
- Every agent is observable: owner, data access, actions, results, risks and cost are visible (spec 027 monitoring, spec 013 cost).

## 2. Scope

**In scope:**
- Agent registry: inventory and ownership of every agent in a tenant.
- Agent definition schema and versioning.
- Platform template library and the 15 standard templates in section 12.
- Agent Studio: create, clone, configure, test, version, submit for review, publish, roll back.
- Agent lifecycle states and publishing rules.
- Evaluation suites as a publishing gate.
- Agent marketplace: discover, request access, favourite, rate, launch.
- Ways employees meet agents: universal assistant routing, marketplace, contextual actions, agent-prepared tasks, proactive briefings.
- Agent memory (session and opt-in persistent).
- Multi-agent orchestration through declared handoffs (R3).
- Agent analytics: adoption, task success, satisfaction and cost.

**Out of scope:**
- Policy, autonomy levels, agent identity, tool registry and gateway, approvals, monitoring, emergency controls and safety tests (spec 027).
- Model routing, budgets and providers (spec 013).
- Retrieval, indexing, citations and the assistant chat surface (spec 014).
- Connectors to other systems (spec 017 and the packs in 018, 019).
- A separate runtime, database or deployment per agent (ADR-0011).

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Use agents they have access to, request access to others, rate agents, manage their agent memory | Agent access policy |
| Agent designer | Create, clone and edit draft agents, run tests, submit for review | `agent.design` on an org scope (built-in role Agent Designer) |
| Agent owner | Business owner named on each agent; answers access requests, receives feedback and alerts | Named on the agent, needs `agent.design` on its scope |
| Agent reviewer / publisher | Review, approve and publish agent versions, roll back | `agent.publish` (held by the AI Policy Administrator by default) |
| AI Policy Administrator | Set tenant agent policy (spec 027), see all agents, suspend and retire | `ai.policy.manage` |
| Security officer | View registry, versions, evaluation results and audit | `agent.view`, `audit.read` |
| Platform operator | Publish platform templates and template updates, force a security update | Platform |

## 4. User scenarios

### User Story 1: Install and adapt a launch template (Priority: P1, R1)

Sara, an agent designer in HR, installs the HR Assistant template, renames it "Acme HR Helper", points it at the HR policies library and the HR services in the catalogue, chooses who can use it, and submits it for review.

**Independent test:** install a template, change name, instructions, knowledge sources and audience, run the test panel, submit, approve and publish, then use it as an employee.

**Acceptance scenarios:**
1. **Given** the template library, **When** Sara opens it, **Then** she sees only templates her package includes, each with purpose, default autonomy level, sources, tools and required connectors, and a "not ready" mark when a required connector is not configured.
2. **Given** an installed template, **When** she edits it, **Then** she can change name, icon, description, instructions, tone, output format, knowledge sources, audience, languages and escalation text, but she cannot add tools, raise the autonomy level or widen data classification beyond what tenant policy and her own permissions allow (spec 027).
3. **Given** knowledge sources, **When** she adds the HR policies library, **Then** only sources she can read are offered, and at run time each employee still sees only content they can read (constitution VII).
4. **Given** the test panel, **When** she asks test questions as herself or as a test persona, **Then** answers, citations, tool calls that would be made and policy decisions are shown, and no write action is executed.
5. **Given** a draft, **When** she submits it, **Then** it moves to In review, edits are locked, and the reviewer gets a task in the inbox (spec 016).

### User Story 2: Review and publish an agent version (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a version in review, **When** the reviewer opens it, **Then** they see a diff against the last published version (instructions, sources, tools, autonomy, audience, model use case), the risk class from spec 027, and the evaluation results.
2. **Given** a version whose mandatory evaluation suites have not passed, **When** anyone tries to publish, **Then** publishing is blocked with the failing cases listed.
3. **Given** a Medium or High risk agent (spec 027), **When** the person who last edited it tries to approve it, **Then** the approval is refused; a different person with `agent.publish` must approve.
4. **Given** a Low risk agent, **When** a user with `agent.design` but not `agent.publish` tries to publish, **Then** it is refused. Editing rights alone never publish an agent.
5. **Given** approval, **When** the version is published, **Then** new conversations use it within 60 seconds, running conversations finish on their version, and the previous version stays available for rollback.

### User Story 3: Roll back, suspend and retire (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a published version 4 with a problem, **When** the publisher rolls back to version 3, **Then** version 3 is checked against the current tenant policy and, if it passes, becomes current without a new review; if it fails, rollback is blocked with the reason.
2. **Given** an agent, **When** the AI Policy Administrator suspends it, **Then** it disappears from the marketplace and the assistant, running executions stop as defined in spec 027, and users see "temporarily unavailable" in their language.
3. **Given** a retired agent, **When** anyone looks for it, **Then** it is not usable, its history and audit stay, and its memory is deleted after the retention period.

### User Story 4: Build a new agent in Agent Studio (Priority: P1, R2)

Omar in Procurement builds a "Supplier onboarding helper" from a blank definition with the no-code Studio.

**Acceptance scenarios:**
1. **Given** `ai.agents.custom` and the tenant's custom agent limit, **When** Omar creates a blank agent, **Then** the Studio walks him through identity, purpose and instructions, model use case, knowledge, tools, access, languages, behavior, autonomy, memory, governance and operations (section 12).
2. **Given** the tool picker, **When** he adds tools, **Then** only tools registered in the tool registry (spec 027), allowed by tenant policy, and within his scope are offered, each with its side effects and approval rule shown.
3. **Given** the limit is reached, **When** he tries to create another, **Then** he is told the limit and who can raise it.
4. **Given** an existing agent, **When** he clones it, **Then** a new draft is created with a new identity and no published history; access, tools and memory are not copied unless he confirms each.

### User Story 5: Test with an evaluation suite (Priority: P1, R1 platform suites, R2 tenant suites)

**Acceptance scenarios:**
1. **Given** a draft, **When** evaluation runs, **Then** the mandatory suites from spec 027 run (permission isolation, prompt injection, unsafe action, data classification) plus the agent's own test set, in Arabic and English (and French, Urdu, Hindi from R2 for tenants with those locales).
2. **Given** an agent test set of questions with expected sources or expected answers, **When** it runs, **Then** each case shows pass or fail, groundedness, citation correctness, tool behavior and the model used, and the overall score is stored with the version.
3. **Given** a score below the tenant threshold (default 85% for the agent test set, 100% for mandatory safety suites), **When** someone tries to publish, **Then** it is blocked.
4. **Given** a published agent, **When** the model behind its use case changes in spec 013, **Then** its evaluation reruns automatically and the owner is alerted if it no longer passes.

### User Story 6: Find and launch agents in the marketplace (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** the marketplace, **When** an employee opens it, **Then** they see agents they can use and agents marked "request access", grouped by category and department, with search, in their language.
2. **Given** an agent marked hidden for people outside its audience, **When** someone outside the audience searches, **Then** it does not appear.
3. **Given** "request access", **When** the employee asks, **Then** the agent owner gets a task in the inbox (spec 016) and the employee sees the outcome.
4. **Given** an agent, **When** the employee favourites it, **Then** it appears in the assistant's agent list and on the home page shortcut strip (spec 008).
5. **Given** an agent they used, **When** they rate it 1 to 5 with an optional comment, **Then** the owner sees ratings and comments; comments are visible only to the owner, the AI Policy Administrator and the employee.

### User Story 7: Reach agents without choosing one (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** the universal assistant (spec 014), **When** an employee asks "how many leave days do I have and can I take next Thursday off?", **Then** the assistant routes to the HR agent the employee can use, shows which agent is answering, and lets the employee switch agent.
2. **Given** no accessible agent fits, **When** the employee asks, **Then** the assistant answers as the Knowledge agent or says it cannot help and suggests the right service.
3. **Given** routing, **When** an agent is chosen, **Then** the assistant gains no tools or data beyond that agent's own, and the routing decision is recorded in the execution record (spec 027).
4. **Given** a policy page or document (specs 009, 011), **When** the employee picks a contextual action (Explain, Summarize, Compare versions, Ask HR), **Then** the configured agent runs with that item as context and the item's permissions apply.

### User Story 8: Agent-prepared tasks and proactive briefings (Priority: P1, R1 tasks, R2 briefings)

**Acceptance scenarios:**
1. **Given** the Workflow and Requests agent prepares a VPN request, **When** the employee is not ready to confirm now, **Then** the draft appears in the employee's task inbox (spec 016) marked "prepared by AI", and nothing is submitted until they confirm.
2. **Given** the Personal Productivity agent, **When** the employee opts in to a daily briefing at 08:00, **Then** each working day they get a notification (spec 015) with their pending tasks, approvals, deadlines and targeted announcements, built only from what they can see at that time.
3. **Given** a briefing, **When** the employee opts out or the agent is suspended, **Then** no further briefings run.
4. **Given** a proactive agent, **When** tenant policy does not allow scheduled agents for its risk class, **Then** the schedule option is not offered.

### User Story 9: Agent memory (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** an agent with session memory only (default), **When** a conversation ends, **Then** nothing from it is used in later conversations.
2. **Given** persistent memory enabled on the agent and accepted by the employee, **When** the agent stores a preference ("answers in Arabic, works in the Riyadh office"), **Then** the employee can view, edit and delete each memory item, and all items are deleted after the agent's retention period (default 90 days without use).
3. **Given** content classified Restricted, **When** the agent tries to store it in memory, **Then** it is refused.
4. **Given** a user who leaves the tenant (spec 003 leaver), **When** they are deprovisioned, **Then** their agent memory is deleted.

### User Story 10: Hand work between agents (Priority: P2, R3)

**Acceptance scenarios:**
1. **Given** the Onboarding agent declares a handoff to the IT Service Desk agent for "laptop and accounts", **When** a new joiner asks for a laptop, **Then** the IT agent takes over that part, the employee is told, and the IT agent runs with its own tools and the employee's permissions.
2. **Given** a handoff not declared in the definition, **When** a model tries it, **Then** it is refused.
3. **Given** a chain of handoffs, **When** it reaches depth 3, **Then** further handoffs are refused and the employee is offered a human contact.
4. **Given** two agents in one conversation, **When** each calls tools, **Then** permissions never add up: each call is checked as that agent for that user (spec 027).

### Edge cases
- Agent owner leaves the company: the agent is flagged, the backup owner or the AI Policy Administrator becomes owner, and the change is audited. The agent keeps running.
- A required connector is removed or loses consent: affected tools are disabled for the agent (spec 027), the agent shows a degraded notice, and the owner is alerted.
- A template update arrives while a tenant draft is in review: the draft is not changed; the owner sees "template update available" after publishing.
- A platform security update to a template: the platform operator can force it; tenant agents built on it get the fix on a set date with 7 days' notice, or immediately for a critical fix.
- AI is disabled for the tenant or all providers are down: the marketplace and assistant show a degraded state; services and pages keep working (constitution XII).
- Two designers edit the same draft: the second save is rejected with a conflict and a diff.

## 5. Functional requirements

- **AGT-001 / FR-001**: Each agent MUST be a versioned definition executed by the common runtime of ADR-0011. The product MUST NOT require code changes, a new deployment or a new database to add or configure an agent. *(AI-008)*
- **AGT-002 / FR-002**: The registry MUST record for each agent: name and description per locale, icon, category, owning org unit, owner and backup owner, business purpose, template origin and version, current version, lifecycle state, risk class and maximum autonomy level (spec 027).
- **AGT-003 / FR-003**: The agent definition MUST cover the configuration areas in section 12, and MUST be validated against the tenant policy of spec 027 on every save and again at publish.
- **AGT-004 / FR-004**: Business configuration (instructions, knowledge, presentation, audience, languages) MUST NOT be able to raise autonomy, add tools, widen classification, change tenant or org scope, or change approval rules beyond policy. *(constitution VII, X, XI)*
- **AGT-005 / FR-005**: The platform MUST provide a template library with platform-versioned templates. Installing a template MUST create a tenant-owned agent linked to the template version. Template updates MUST be offered, not applied, except forced security updates.
- **AGT-006 / FR-006**: Lifecycle states MUST be Draft, In review, Published, Suspended and Retired, with a test panel available in Draft. Published versions MUST be immutable.
- **AGT-007 / FR-007**: Publishing MUST require `agent.publish`, passing mandatory evaluation suites, and for Medium and High risk agents an approver different from the last editor.
- **AGT-008 / FR-008**: Rollback MUST make a previous version current after an automatic policy check, without a new review if the check passes.
- **AGT-009 / FR-009**: Evaluation MUST support agent test sets with expected sources or answers, run the mandatory suites of spec 027, report per-case results and a score per locale, store results with the version, and rerun when the model for the agent's use case changes.
- **AGT-010 / FR-010**: The marketplace MUST list agents by access policy with search, categories, favourites, ratings, request access through the task inbox, and launch. *(R2)*
- **AGT-011 / FR-011**: The universal assistant MUST route each request to at most one agent at a time from those the user can use, show which agent answers, allow switching, and record the routing decision. *(spec 014)*
- **AGT-012 / FR-012**: Contextual actions MUST be configurable per content type and MUST pass the item as context under the item's permissions.
- **AGT-013 / FR-013**: Agent-prepared drafts and approvals MUST appear in the task inbox of spec 016 and MUST NOT take effect until a person acts (spec 027 autonomy rules).
- **AGT-014 / FR-014**: Proactive agents MUST be opt-in per user, run as that user at run time, follow tenant policy on scheduled agents, and deliver through notifications (spec 015). *(R2)*
- **AGT-015 / FR-015**: Persistent memory MUST be off by default, opt-in per agent and per user, viewable and deletable by the user, classified, never Restricted, and deleted on leaver and after retention. *(R2)*
- **AGT-016 / FR-016**: Handoffs between agents MUST be declared in the definition, limited to depth 3, and each agent MUST act with its own tools and the user's permissions. *(R3)*
- **AGT-017 / FR-017**: Agents MUST answer in the user's interface language when the agent supports it, with instructions and test sets kept per locale. Arabic and English MUST be supported for all launch templates in R1. *(spec 007)*
- **AGT-018 / FR-018**: Agent analytics MUST show active users, conversations, task success (resolved without escalation), user ratings, escalations, tool success and failure, and cost from spec 013, per agent and version. *(R1 basic, R2 detailed through spec 021)*
- **AGT-019 / FR-019**: The number of published agents and of customer-created agents MUST be limited by entitlements (`ai.agents.max_published`, `ai.agents.max_custom`).
- **AGT-020 / FR-020**: Templates MUST declare their required capability key, required connectors and minimum autonomy needed, so the library can show which ones the tenant can use.

## 6. Business and validation rules
- An agent must always have an owner and an owning org unit.
- Agent names must be unique per tenant per locale.
- The audience of an agent cannot be wider than the owner's org scope unless the AI Policy Administrator approves a tenant-wide audience.
- An agent with write tools must name at least one escalation contact (person or service).
- A retired agent cannot be restored; it can be cloned into a new draft.

## 7. Tenant and organization scope
- Agents, versions, test sets, evaluation results, ratings, memory and analytics are tenant-owned with RLS. Templates are platform-level.
- Cross-tenant: a tenant agent cannot be shared to another tenant. Platform templates contain no tenant data.
- Org boundary: designers and owners work within their scope; an agent owned by Acme Gulf cannot use knowledge sources or tools outside Acme Gulf unless granted by the tenant (spec 027 cross-organization rule).

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Template | Product (platform) | Internal | Versions kept | No |
| Agent definition and versions | Product | Internal | Life of tenant, versions kept | Owner names |
| Test sets and evaluation results | Product | Highest classification of their sources | Life of agent | Possibly |
| Ratings and comments | Product | Internal | 2 years | Yes |
| Persistent memory items | Product | Per item, never Restricted | Agent retention, default 90 days unused | Yes |
| Conversations | Spec 014 | Per content | Spec 014 | Yes |
| Execution records | Spec 027 | Confidential | Spec 027 | Yes |

## 9. Package entitlements

Package names map to the editions in the governance plan: Workplace (Essential), Workplace Plus (Business), Enterprise & Government (Enterprise).

| Capability key | Workplace | Workplace Plus | Enterprise & Gov |
|---|---|---|---|
| `ai.agents.basic` | Six launch templates, basic configuration | Yes | Yes |
| `ai.agents.department` | No | Department templates (7 to 12) | Yes |
| `ai.agents.advanced` | No | No | Advanced templates (13 to 15) |
| `ai.agents.custom` | No | Yes, limited by `ai.agents.max_custom` | Yes |
| `ai.agents.marketplace` | Yes | Yes | Yes |
| `ai.agents.orchestration` | No | Platform-defined handoffs only | Yes |
| `ai.agents.max_published` | Quota (commercial data) | Quota | Quota |
| `analytics.advanced` | Basic agent analytics | Detailed | Advanced |

Governance features in spec 027 have a mandatory baseline in every package.

## 10. UX, localization and accessibility
- Pages: Agent registry (admin), Template library, Agent Studio, Review, Marketplace, My agents (favourites and memory).
- Agent Studio is a form-based editor in sections, never a code editor; an advanced JSON view is read-only for administrators.
- Every agent answer shows the agent name and a label that it is AI generated, with feedback controls.
- Arabic and English for all strings, agent names, descriptions and instructions in R1; layouts mirror in RTL; full keyboard support; WCAG 2.2 AA.

## 11. AI behavior
- The runtime builds context in this order: platform safety instructions, tenant policy instructions, agent instructions, authorized retrieved content marked as untrusted data, user message (spec 027).
- Agents cite sources for factual answers about tenant content and say when they have no evidence.
- Agents never claim an action happened unless the tool gateway returned success.

## 12. Agent definition and template library

### Configuration areas

| Area | Settings |
|---|---|
| Identity | Name, icon, description, category, department, owner, backup owner, version |
| Instructions | Purpose, system instructions, role, prohibited behaviors, escalation text |
| AI model | Use case from spec 013 (which sets model and fallback through tenant policy); inference limits within policy |
| Knowledge | Approved sources and collections, metadata filters, citation rules (spec 014) |
| Tools | Tools from the registry (spec 027), with per-agent limits on calls per run and per day |
| Access | Audience by tenant, org unit, department, group and person; hidden or request-access |
| Languages | Supported locales from ar, en, fr, ur, hi |
| Behavior | Tone, output format, confidence handling, when to hand to a human |
| Autonomy | L0 to L4, capped by tenant policy and package (spec 027) |
| Memory | Session only, or persistent with retention |
| Governance | Risk class (computed), approval rules per tool (spec 027), audit level |
| Operations | Budget (spec 013), concurrency, timeouts, alert recipients |

### Standard templates

All templates run on the common runtime. They are configurations and capability packages, not separate engines.

| # | Template | Release | Capability key | Default autonomy | Sources and tools | Needs |
|---|---|---|---|---|---|---|
| 1 | Enterprise Knowledge | R1 | `ai.agents.basic` | L0 | Pages, policies, documents, FAQs (009, 011, 012, 014); SharePoint from R2 (018) | 014 |
| 2 | HR Assistant | R1 | `ai.agents.basic` | L2 | HR policies, HR form services (016); HR record and balances from R2 (019, 022) | 016 |
| 3 | IT Service Desk | R1 | `ai.agents.basic` | L2 | IT knowledge base, Help Desk form service (016); ITSM ticket create and status from R2 (017) | 016 |
| 4 | Content and Writing | R1 | `ai.agents.basic` | L1 | Writing guidelines, templates, documents the user can read; summarize, translate, draft, rewrite | 014 |
| 5 | Workflow and Requests | R1 | `ai.agents.basic` | L2 | Service catalogue, request status, task explanations (016, 024) | 016 |
| 6 | Personal Productivity | R1 | `ai.agents.basic` | L1 | Tasks, approvals, targeted announcements; calendar and mail from R2 (018) | 015, 016 |
| 7 | Employee Onboarding | R2 | `ai.agents.department` | L2 | Onboarding journeys, checklists, FAQs, service requests | 016, 019 |
| 8 | People Directory and Expertise | R2 | `ai.agents.department` | L0 | Directory, skills, org chart (010) | 010 |
| 9 | Learning and Development | R2 | `ai.agents.department` | L1 | Learning library, training requests; LMS through connector | 016, 017 |
| 10 | Procurement | R2 | `ai.agents.department` | L3 | Procurement policy, purchase requests, supplier onboarding, status from ERP | 016, 017, 024 |
| 11 | Finance and Expenses | R2 | `ai.agents.department` | L2 | Expense policy, claim preparation, reimbursement status | 016, 017 |
| 12 | Internal Communications | R2 | `ai.agents.department` | L1 (publishing needs owner approval) | Draft announcements, propose audiences, summarize feedback, multilingual versions | 009, 023 |
| 13 | Legal and Compliance | R3 | `ai.agents.advanced` | L0 | Policy interpretation, contract review help, compliance evidence retrieval | 011, 025, 026 |
| 14 | Executive Intelligence | R3 | `ai.agents.advanced` | L0 | Department KPIs, risks, activities, executive briefings | 021 |
| 15 | Process Automation | R3 | `ai.agents.advanced` | L4 within approved processes | Multi-step workflows across applications, run as BPMN processes (024) | 017, 024 |

## 13. Audit

| Event | Type | Key fields |
|---|---|---|
| agent.created / cloned / installed_from_template | audit | agent, template version, actor |
| agent.version_submitted / approved / rejected / published | audit | agent, version, actor, risk class, evaluation score |
| agent.rolled_back / suspended / resumed / retired | audit | agent, from and to version, actor, reason |
| agent.owner_changed / audience_changed | audit | agent, old and new, actor |
| agent.evaluation_run | operational | agent, version, suite, score |
| agent.access_requested / granted / denied | audit | agent, requester, decider |
| agent.memory_deleted | audit | agent, user, actor (user or system) |

## 14. Security and abuse cases
- Designer smuggles a tool or wider scope through instructions: instructions have no effect on tools, scope or approvals (FR-004); policy check at save and publish.
- Self-approval of a risky agent: separation of editor and approver for Medium and High risk (FR-007).
- Malicious template update: platform templates are signed and versioned; forced updates are audited and announced.
- Knowledge source poisoning to change agent behavior: retrieved content is untrusted data (spec 027); evaluation reruns detect drift.
- Rating comments used to harass owners: comments are private and can be reported.
- Memory as a covert data store across users: memory is per user per agent, never shared, never Restricted.

## 15. Non-functional requirements
- Publishing a version takes effect within 60 seconds.
- The registry supports 500 agents and 5,000 versions per tenant.
- Routing adds under 300 ms at p95 to the first token in the assistant.
- Evaluation of a 100-case test set completes in under 15 minutes.

## 16. Success criteria
- **SC-001**: A tenant can install, adapt, review and publish the six launch templates in one working day without developer help.
- **SC-002**: Every agent in a tenant has an owner, a version and a risk class visible in the registry, checked by an automated test.
- **SC-003**: Adding a new standard template to the library needs no change to the runtime code, only a new template definition and its tests.

## 17. Open questions
1. Default publishing approver: AI Policy Administrator, or a separate Agent Publisher role? Default: AI Policy Administrator holds `agent.publish`; tenants can grant it to others.
2. Can Workplace tenants create agents from scratch? Default: no, they adapt templates only (governance plan packaging).
3. Should ratings be visible to all users of an agent? Default: average score visible, comments private.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| AI-007 (agent part) | Templates 4 and 1, US7 |
| AI-008 | AGT-001 to AGT-009, US1 to US5 |
| UC-07 | US7, US8, spec 027 |
| Governance plan epics 1, 2, 3, 4, 12, 14, 15 | AGT-001 to AGT-020 |
