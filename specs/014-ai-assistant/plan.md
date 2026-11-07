# Implementation Plan: AI Assistant and Retrieval-Augmented Answers

**Spec:** `specs/014-ai-assistant/spec.md` (v0.1, Proposed) | **Date:** 2026-11-07
**Shared base:** ADR-0011 (`Agents` module), spec 012, 013, 020, 027 plans

## Summary
The assistant lives in the `Agents` module as the platform "knowledge" agent plus a router: each turn calls `ISearchRetrieval` as the user, builds a prompt with passages in the untrusted envelope, calls `IAiGateway` with use case `assistant`, checks that every cited passage ID was among those retrieved, and streams the answer. When the router picks an agent (spec 020), the turn runs as that agent. Conversations are stored in `agents.conversation` and `agents.turn` with the tenant retention job.

## API (v1)
`POST /api/v1/assistant/turns` (streamed), `GET /api/v1/assistant/conversations`, `DELETE /api/v1/assistant/conversations/{id}`, `DELETE /api/v1/assistant/conversations`, `POST /api/v1/assistant/turns/{id}/feedback`.

## Tests
Citation check against retrieved IDs; no evidence answer; policy-blocked evidence; hidden content never cited (persona fixtures); routing only to permitted agents; history isolation and retention; degraded states; streaming. Acceptance (e2e): panel, cited answer, feedback, in English and Arabic with axe.
