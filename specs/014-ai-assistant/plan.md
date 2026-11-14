# Implementation Plan: AI Assistant and Retrieval-Augmented Answers

**Spec:** `specs/014-ai-assistant/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0011 (`Agents` module), spec 012, 013, 020, 027 plans

## Summary
The assistant lives in the `Agents` module as the platform "knowledge" agent plus a router: each turn calls `ISearchRetrieval` as the user, builds a prompt with passages in the untrusted envelope, calls `IAiGateway` with use case `assistant`, checks that every cited passage ID was among those retrieved, and streams the answer. When the router picks an agent (spec 020), the turn runs as that agent. Conversations are stored in `agents.conversation` and `agents.turn` with the tenant retention job.

## API (v1)
`POST /api/v1/assistant/turns` (streamed), `GET /api/v1/assistant/conversations`, `DELETE /api/v1/assistant/conversations/{id}`, `DELETE /api/v1/assistant/conversations`, `POST /api/v1/assistant/turns/{id}/feedback`.

## Tests
Citation check against retrieved IDs; no evidence answer; policy-blocked evidence; hidden content never cited (persona fixtures); routing only to permitted agents; history isolation and retention; degraded states; streaming. Acceptance (e2e): panel, cited answer, feedback, in English and Arabic with axe.

## R2 plan
**Date:** 2026-11-14. R2 adds US5 to US8 and AST-015 to AST-022. AST-019 (search summaries for 012) and AST-021 (model, prompt version and retrieval configuration on each turn) were built with spec 012 R2 and the R1 turn record, so they need tests only.

R2 slices, in order:
1. **Guests and context exclusions (AST-020, AST-022):** the assistant settings gain `guestsAllowed` (off by default). A guest is refused on every assistant endpoint unless it is on; when it is on, retrieval already runs as the guest, so only the spaces they were invited to can be cited. Payslips (019) and anonymous engagement content (023) are never indexed by 012; the turn pipeline also drops any passage whose source kind is on a deny list, so a future source cannot leak them by mistake.
2. **Personal context answers (US7, AST-017):** self-scoped agent tools registered in the 027 tool registry: `my_leave_balance` (022, through `ILeaveBalances`) and `my_open_requests` (016, through the services contract). Each reads only the calling person's records; a question about someone else is answered by the tool's refusal unless spec 004 lets the caller see that person's record. Tool results carry their source and time, which the answer shows. A platform template "My HR helper" uses them, so routing picks it for such questions with no assistant code.
3. **Files in a conversation (US5, AST-015):** `POST /api/v1/assistant/conversations/{id}/files` (multipart, PDF, Word, text and images, up to 20 MB) behind `ai.assistant.file_upload`. The file is scanned (`IMalwareScanner`), its text extracted (Documents text extraction, Tika for PDFs and images), and the person picks its classification (default Internal). A classification above what the AI policy allows for the `assistant` use case is refused with the reason. The file and its text live in `agents.conversation_file` and conversation-scoped storage, are used as untrusted passages only in that conversation's turns (cited as the file), raise the turn's classification, and are deleted with the conversation, by the person or by retention.
4. **Voice (US6, AST-016):** the gateway gains `transcribe` and `speak` use cases and catalogue capabilities `transcription` and `speech`, served by the OpenAI-compatible audio endpoints (`/audio/transcriptions`, `/audio/speech`) of OpenRouter or a private endpoint, metered like any call. `POST /api/v1/assistant/transcribe` (audio up to 60 seconds) returns text for the person to edit; nothing is sent until they confirm. `POST /api/v1/assistant/speak` returns the answer's audio in its language. Audio is never written to storage or logs. Both need `ai.assistant.voice`.
5. **Teams (US8, AST-018):** the personal app depends on the TeamBeats Teams app of spec 015 US5, built with spec 015 in the next R2 group. The assistant endpoints already take any authenticated TeamBeats session, so the Teams app will call them with a session exchanged from Teams SSO; nothing assistant-specific is built before it.

### R2 data model (schema `agents`)
- `assistant_settings` gains `guests_allowed`.
- `conversation_file` (conversation, owner, name, media type, size, classification, extracted text, storage key, created at); deleted with its conversation.

### R2 API
- `POST /api/v1/assistant/conversations/{id}/files`, `GET` and `DELETE /api/v1/assistant/conversations/{id}/files/{fileId}`; a turn request may name `fileIds`.
- `POST /api/v1/assistant/transcribe`, `POST /api/v1/assistant/speak`.
- Settings: `guestsAllowed` on the existing assistant settings.

### R2 tests
Guests refused by default and limited to their spaces when allowed; denied source kinds never cited; personal tools read only the caller's records and refuse others; upload scanned, infected refused, classification ceiling refused, file text cited only in its conversation and deleted with it and by retention; voice transcription and speech through the gateway, refused without the capability, nothing stored; cross-tenant denial for every new endpoint. e2e: upload and voice controls in English and Arabic with axe.
