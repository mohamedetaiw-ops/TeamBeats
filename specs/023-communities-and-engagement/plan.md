# Implementation Plan: Communities and Engagement

**Spec:** `specs/023-communities-and-engagement/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0013, ADR-0006, spec 009 plan (editor, comments and reactions), spec 012 plan (index events), spec 015 plan (notification requests)

## Summary
A new `Engagement` module (planned in the module map) owns communities, posts, polls, surveys, recognition, points and celebrations. It reuses the 009 rich text editor component and the 011 storage port for media. Anonymous items are written through a separate use case that never receives the user ID beyond the authorization check, and writes to tables with no user column. Search and notifications are reached through events and the 015 contract.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I tenant isolation | Yes | All tables tenant-owned with forced RLS; anonymous hash keys per tenant |
| III authorization | Yes | Owner and moderator relations in 004; permissions through `IAuthorizationService` |
| V entitlements | Yes | Section 9 keys |
| Privacy | Yes | Anonymous tables have no user column; responded markers in a separate table deleted at close |
| Rule 5 | Yes | Profile and org data from 010 and 002 contracts |

## Data model (schema `engagement`)
- `community`, `community_member`, `channel`, `post`, `comment`, `reaction`, `hashtag`, `report`, `mute`
- `anon_post` (no user column, day-level time), `poll`, `poll_vote`, `survey`, `survey_question`, `survey_response` (named or anonymous variants), `survey_responded` (deleted at close)
- `recognition`, `badge`, `value`, `points_ledger`, `period_award`, `celebration_optin`, `album`, `album_item`

## Key decisions
- Department membership is computed from 002 events into `community_member` with `source = org_unit`.
- Threshold checks run in the query layer so no API returns a below-threshold group.
- Image content safety goes through the 026 scanner port; no new service.
- Feeds use keyset pagination on `(pinned, published_at, id)`.

## Tests
Cross-tenant mention and search denial; closed community invisibility; anonymous storage review test that asserts no user identifier column or log field; threshold hiding in views, filters and exports; points caps and self recognition refusal; moderation flows; entitlement off paths.
