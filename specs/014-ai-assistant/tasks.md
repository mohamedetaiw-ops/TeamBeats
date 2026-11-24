# Tasks: AI Assistant and Retrieval-Augmented Answers

Depends on 008, 012, 013, 020, 027, 034. R1: US1 to US4. R2: US5 to US8.

- [x] T001 Migration in `Agents` for conversations, turns and feedback (forced RLS); retention job
- [x] T002 [P] [US1] Tests: citations only from retrieved passages; no evidence; policy block; hidden content never cited; answer language
- [x] T003 [US1] Turn pipeline: retrieval as the user, prompt with untrusted envelope, gateway call, citation check, streaming
- [x] T004 [US2] Routing to agents and confirmation cards in the conversation
- [x] T005 [US3] Summarize, explain, translate, rewrite and draft on permitted items and pasted text
- [x] T006 [US4] History, delete, retention, feedback and degraded states
- [x] T007 Assistant panel and page with streaming, citations and feedback, in English and Arabic, RTL; e2e with axe
- [x] T008 Platform evaluation suite for the assistant in spec 034; OpenAPI; R1 implementation notes; spec evidence

## R2
- [x] T009 [P] Tests: guests refused unless allowed and then only over their spaces; denied source kinds never cited; search summary and turn record evidence (AST-019, AST-021)
- [x] T010 Assistant setting for guests and the source kind deny list (AST-020, AST-022)
- [x] T011 [P] [US7] Tests: personal tools read only the caller's records, refuse others unless 004 allows, show source and time
- [x] T012 [US7] Self-scoped `my_leave_balance` and `my_open_requests` tools and the "My HR helper" template
- [x] T013 [P] [US5] Tests: scan, classification ceiling, use only in the conversation, deletion with the conversation and by retention
- [x] T014 [US5] File upload in conversations, panel controls, in English and Arabic, RTL; e2e with axe
- [x] T015 [US6] Voice input and read aloud through gateway `transcribe` and `speak`; panel controls; tests
- [x] T016 [US8] Assistant as a personal chat with the TeamBeats bot (one bot route for Teams, routing by directory and linked person, answer cards with sources, continued conversations); tests
- [x] T017 OpenAPI; R2 implementation notes; spec evidence
