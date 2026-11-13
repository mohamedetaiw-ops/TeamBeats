# Feature Specification: AI Assistant and Retrieval-Augmented Answers

| Field | Value |
|---|---|
| Spec ID | 014-ai-assistant |
| Area | ai |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (universal assistant panel, cited answers from permitted content, routing to agents, conversation history, feedback, Q&A, summarize, translate, rewrite and draft), R2 (AI summary of search results for spec 012, file upload in a conversation, voice input and read aloud, French, Urdu and Hindi, assistant in Microsoft Teams, personal context answers such as my leave balance and my open requests through agents), R3 (proactive suggestions across the intranet) |
| Last updated | 2026-11-08 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | AI-005, AI-007, KNO-007, AST-001 to AST-022 (new) |
| Use cases | UC-06 |
| Depends on | 008 (shell), 012 (retrieval), 013 (AI Gateway), 020 (routing to agents), 027 (governance), 034 (gate on routing and RAG configuration), 007 |
| Used by | 009, 011, 025 (contextual actions and policy Q&A), 012 (search summaries, R2), 015 (Teams, R2), 035 (the `ask_teambeats` MCP tool and the `assistant:ask` scope give cited answers to external AI clients, R2) |

## 1. Problem and outcome

**Problem:** Employees ask the same questions again and again ("how many leave days do I have", "what is the travel policy", "how do I get VPN access"), and answers are spread across documents, policies and services. An assistant that answers from the open web, or from content the person cannot read, is unsafe.

**Outcome:** One assistant panel is available on every page. It answers from content the person may read, cites its sources, says when it found nothing, and hands work to the right agent (spec 020) without gaining any tools of its own. Every answer goes through the AI Gateway and the governance layer.

## 2. Scope

**In scope:**
- Assistant panel in the shell and a full page, in Arabic and English.
- Q&A with citations from retrieval (spec 012) filtered as the user. *(AI-005, KNO-007)*
- Summarize, translate, rewrite and draft on text the user provides or an item they can read. *(AI-007)*
- Routing to agents the user may use (spec 020 US7), with the answering agent shown and a way to switch.
- Conversation history per user with retention, delete and "start new".
- Feedback (helpful or not, with an optional reason), feeding evaluation (spec 034, R2 review queues) and knowledge gaps (spec 011 US4).
- Degraded states for no evidence, blocked by policy, provider unavailable, quota exhausted.

- R2: file upload in a conversation (scanned by 026, classified, kept with the conversation and deleted with it).
- R2: voice input and read aloud through speech models on the AI Gateway.
- R2: French, Urdu and Hindi answers, following 007 locales.
- R2: the assistant inside Microsoft Teams as a personal app, with the same rules.
- R2: personal context answers ("how many leave days do I have") by routing to agents whose tools read the user's own records (022, 016).
- R2: summary of search results for spec 012.

**Out of scope:**
- Tools of its own: the assistant holds none (spec 020); actions come from agents under spec 027.
- Proactive suggestions (R3).
- Answers from the open web.

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Use the assistant, see and delete their own history, give feedback | `ai.assistant` capability and active membership |
| AI Policy Administrator | Set retention of conversations and which use cases are on | `ai.policy_manage` |
| Security officer | See execution records (spec 027), not conversation text unless the logging policy allows | `agent.security_view` |

## 4. User scenarios

### User Story 1: Ask a question and get a cited answer (Priority: P1, R1)

Ahmed asks "Can I carry over unused leave?". The assistant answers in Arabic from the leave policy clause that applies to his unit, cites the clause, and links to it.

**Acceptance scenarios:**
1. **Given** permitted content that answers the question, **When** the user asks, **Then** the answer cites each source with title, version and a link to the passage, and every claim is backed by a cited passage.
2. **Given** no permitted evidence, **Then** the assistant says it found nothing the user can see, and does not answer from general knowledge as if it were company information.
3. **Given** evidence the AI policy blocks for this classification, **Then** the assistant says some content cannot be processed by AI under the organization's policy, without naming it.
4. **Given** content the user cannot read, **Then** it is never retrieved, cited, named or summarized. *(constitution VII)*
5. **Given** the user writes in Arabic, **Then** the answer is in Arabic, and English sources are cited with their titles.

### User Story 2: Get help from the right agent (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a question that fits an agent the user may use, **Then** the assistant routes to it, shows the agent's name, and lets the user switch or go back.
2. **Given** an agent proposes an action, **Then** the confirmation card of spec 027 appears in the conversation and nothing happens until the user confirms.
3. **Given** no agent fits, **Then** the assistant answers as the knowledge agent or suggests the right service.

### User Story 3: Work with text (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** a page or document the user can read, **When** they choose Summarize, Explain or Translate, **Then** the assistant works only on that item and cites it.
2. **Given** text the user pastes, **When** they ask for a rewrite or draft, **Then** the result is labelled as AI generated and nothing is published or sent.

### User Story 4: History, feedback and degraded states (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** past conversations, **Then** the user sees their own only, can delete one or all, and history ends after the tenant retention period (default 30 days).
2. **Given** feedback on an answer, **Then** it is stored with the execution record reference and counted for the agent owner.
3. **Given** the provider is unavailable or the quota is used up, **Then** the panel shows the matching message and the rest of the page keeps working.

### User Story 5: Ask about a file I bring (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** Laila uploads a PDF up to 20 MB, **Then** it is scanned, classified by the user (default Internal), and used only in that conversation.
2. **Given** the file's classification is above what the AI policy allows for the assistant, **Then** the upload is refused with the reason.
3. **Given** the conversation is deleted or expires, **Then** the file and its extracted text are deleted with it.

### User Story 6: Talk instead of type (Priority: P3, R2)

**Acceptance scenarios:**
1. **Given** voice is on for the tenant, **When** Omar holds the microphone button, **Then** his speech is transcribed through the AI Gateway, shown for him to edit, and sent only when he confirms.
2. **Given** read aloud, **Then** the answer is spoken in its language, and citations remain visible.
3. **Given** audio, **Then** it is not stored after transcription.

### User Story 7: Ask about my own things (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** an HR agent with a "my leave balance" tool (022), **When** Ahmed asks how many leave days he has, **Then** the assistant routes to it, the tool reads only Ahmed's record, and the answer shows the source and time.
2. **Given** a question about another person's record, **Then** the tool refuses unless Ahmed may see it under 004, and the assistant says so.

### User Story 8: Use the assistant in Teams (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** the TeamBeats Teams app (015 US5), **Then** the assistant is available as a personal chat with the same retrieval, citations, routing and history as in TeamBeats.
2. **Given** an action confirmation, **Then** it opens in TeamBeats when step-up is required.

### Edge cases
- A source changes between retrieval and display: the citation links to the version used and says if a newer one exists.
- Very long conversations: older turns are summarized or dropped by the assistant before the context limit (spec 013 leaves trimming to the caller).

## 5. Functional requirements

- **AST-001 / FR-001**: The assistant MUST be available from every page in the shell and as a full page.
- **AST-002 / FR-002**: Retrieval MUST run as the user through spec 012 before any prompt is assembled. *(AI-005)*
- **AST-003 / FR-003**: Answers MUST cite sources at passage level where the source supports it, and MUST say when no permitted evidence exists. *(KNO-007)*
- **AST-004 / FR-004**: Every model call MUST go through the AI Gateway with the highest classification of the context. *(constitution VIII, IX)*
- **AST-005 / FR-005**: The assistant MUST hold no tools; actions come only from routed agents under spec 027.
- **AST-006 / FR-006**: Routing MUST consider only agents the user may use and MUST be recorded in the execution record.
- **AST-007 / FR-007**: Summarize, explain, translate, rewrite and draft MUST work on items the user can read or text they provide, and generated text MUST be labelled. *(AI-007)*
- **AST-008 / FR-008**: Conversation history MUST be per user, deletable by the user, with tenant retention (default 30 days).
- **AST-009 / FR-009**: Feedback MUST be stored with the execution record reference.
- **AST-010 / FR-010**: Retrieved content MUST be passed as untrusted data (spec 027 US5).
- **AST-011 / FR-011**: Degraded states MUST be shown for no evidence, policy block, provider unavailable and quota exhausted, keyed by the gateway's typed result.
- **AST-012 / FR-012**: Answers MUST be in the user's language unless they ask otherwise.
- **AST-013 / FR-013**: Routing and RAG configuration changes MUST pass the spec 034 gate.
- **AST-014 / FR-014**: Answer rendering MUST not load external images and MUST show links outside the tenant allowlist as text.
- **AST-015 / FR-015**: Uploaded files MUST be scanned, classified, used only in their conversation, refused above the AI policy ceiling, and deleted with the conversation.
- **AST-016 / FR-016**: Voice input MUST be transcribed through the AI Gateway, shown for confirmation before sending, and audio MUST NOT be stored.
- **AST-017 / FR-017**: Personal context answers MUST come only from agent tools that read records the user may see, with source and time shown.
- **AST-018 / FR-018**: The Teams assistant MUST use the same contracts, history and rules as the web assistant.
- **AST-019 / FR-019**: Search summaries for 012 MUST use only the passages 012 retrieved for the user.
- **AST-020 / FR-020**: Payslips (019) and anonymous engagement content (023) MUST never enter the assistant's context.
- **AST-021 / FR-021**: Each answer MUST record the model, prompt version and retrieval configuration in the execution record for evaluation (034).
- **AST-022 / FR-022**: Guests (026) MUST NOT use the assistant unless the tenant enables it for guests, and then only over spaces they were invited to.
- **FR-023**: Deleting all conversations MUST be offered from a menu on the conversation list (or the assistant settings), styled as a destructive menu item, and confirmed in a dialog that states the number of conversations to delete (DLS-084). Acceptance: the assistant page shows no delete all button in the main view; the dialog says "Delete 12 conversations?" when there are 12.

## 6. Business and validation rules
- Maximum question length 2,000 characters.
- Citations point to the version used.

## 7. Tenant and organization scope
- Conversations and feedback are tenant-owned with RLS and visible only to their user.
- Retrieval follows each item's permissions; org boundaries come from spec 012.
- Cross-tenant: no retrieval, routing or history across tenants.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Conversation and turns | Product | Highest classification of its content | Tenant setting, default 30 days | Yes |
| Feedback | Product | Internal | 25 months | Yes |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `ai.assistant` | on | on | on |
| `ai.assistant.monthly_requests` (limit per user) | 50 | 300 | 1,000 |
| `ai.assistant.file_upload` (R2) | off | on | on |
| `ai.assistant.voice` (R2) | off | on | on |
| `ai.assistant.teams` (R2) | on | on | on |

## 10. UX, localization and accessibility
- Panel opens from the shell with a keyboard shortcut; answers stream; citations are links with text, not only icons; RTL.

## 11. AI behavior
Covered above: retrieval first, citations, no general-knowledge answers presented as company facts, untrusted content handling, no tools.

## 12. Integrations and events
Publishes `AssistantAnswered` (IDs, agent, outcome) for analytics. Consumes nothing.

## 13. Audit

| Event | Type | Key fields |
|---|---|---|
| assistant.history_deleted | audit | user, scope |
| assistant.retention_changed | audit | old, new, actor |

## 14. Security and abuse cases
- Prompt injection through retrieved content: untrusted envelope and no tools.
- Data exfiltration through links or images in answers: FR-014.
- Asking about hidden people or documents: retrieval as the user returns nothing.

## 15. Non-functional requirements
- First streamed token under 2 seconds p95 excluding provider queueing.

## 16. Success criteria
- **SC-001**: Every answer in the release suite cites only passages the persona can read.
- **SC-002**: In the pilot, 70% of rated answers are marked helpful.

## 17. Open questions
- [NEEDS CLARIFICATION: should conversation history be kept longer than 30 days?] Default: 30 days, tenant configurable up to 1 year.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| AI-005 | AST-002, US1 |
| AI-007 | AST-007, US3 |
| KNO-007 | AST-003, US1 |
| UC-06 | US1 to US4, US7 |
| R2 additions | AST-015 to AST-022, US5 to US8 |
