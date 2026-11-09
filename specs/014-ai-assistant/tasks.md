# Tasks: AI Assistant and Retrieval-Augmented Answers

Depends on 008, 012, 013, 020, 027, 034. R1: US1 to US4. R2: US5 to US8.

- [x] T001 Migration in `Agents` for conversations, turns and feedback (forced RLS); retention job
- [x] T002 [P] [US1] Tests: citations only from retrieved passages; no evidence; policy block; hidden content never cited; answer language
- [x] T003 [US1] Turn pipeline: retrieval as the user, prompt with untrusted envelope, gateway call, citation check, streaming
- [ ] T004 [US2] Routing to agents and confirmation cards in the conversation
- [ ] T005 [US3] Summarize, explain, translate, rewrite and draft on permitted items and pasted text
- [x] T006 [US4] History, delete, retention, feedback and degraded states
- [ ] T007 Assistant panel and page with streaming, citations and feedback, in English and Arabic, RTL; e2e with axe
- [ ] T008 Platform evaluation suite for the assistant in spec 034; OpenAPI; R1 implementation notes; spec evidence

## R2
- [ ] T013 [P] [US5] Tests: scan, classification ceiling, deletion with the conversation
- [ ] T014 [US5] File upload in conversations
- [ ] T015 [US6] Voice input and read aloud
- [ ] T016 [US7] Self-scoped HR and request tools with 022 and 016
- [ ] T017 [US8] Teams personal app
