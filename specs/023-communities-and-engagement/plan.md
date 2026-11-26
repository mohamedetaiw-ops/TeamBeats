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

## R3 plan (rooms, mirroring, AI summaries, sentiment trends)
**Date:** 2026-11-26. R3 adds User Stories 9 to 11 and FR-027 to FR-033.

R3 slices, in order:
1. **Discussion rooms (US9, FR-027 to FR-029, `engagement.rooms`):** `engagement.room` (community, names, status `active` or `archived`) and `engagement.room_message` (room, author, body up to 2,000 characters, status like a comment, held reason, pending mentions, removal fields). Owners and engagement administrators open and archive rooms; reading and writing follow the community standing. Mentions, the keyword filter, reports (target type `room_message`, 3 reports hide) and moderator removal reuse the comment rules, so held, hidden and reported messages show in the moderator queue. Live delivery is server-sent events on `GET /api/v1/engagement/rooms/{id}/stream`: the endpoint checks access, then every 2 seconds (sooner when a message is sent on the same instance) reads new messages in a fresh scope as the same person, checking access again each time, and ends after 2 minutes so the browser reconnects with its session checked again. No new infrastructure: messages are read from Postgres. Page `/communities/[id]/rooms/[roomId]`, and the rooms list on the community page.
2. **Mirroring (US10, FR-030, `engagement.mirroring`):** `engagement.mirror` (community, target `slack` or `teams`, channel id, excerpt flag, last sent and last failure). Only engagement administrators set it, only for open, public or internal communities without a unit or space. A `PostPublished` handler sends the notice: Slack through `ISlackMessaging.ShareAsync`; Teams through a new `ITeamsMessaging.PostToChannelAsync`, which starts a thread in the channel through the bot (Bot Framework `v3/conversations` with the channel in the channel data). One notice per post (key on the post id). Removal does not recall a sent notice; without the excerpt nothing of the post leaves TeamBeats but its link.
3. **Summaries (US11 scenarios 1 and 2, FR-031, FR-032):** `ThreadSummaries` follows the spec 016 request summary: the post and its visible comments, or the last 200 visible room messages, as untrusted JSON to `IAiGateway` with use case `summarize` and the community's classification; needs `ai.assistant`; nothing stored. Anonymous posts are refused (`conflict.engagement.ai_anonymous_off`) unless `engagement.ai_setting.anonymous_content` is on, and are passed without an author. The setting is managed on the engagement settings by `engagement.manage` at tenant scope and audited.
4. **Community sentiment trends (US11 scenario 3, FR-033):** spec 021 gains a topic key on `analytics.sentiment` (the community id for engagement sources) and a contract `IAnalyticsSentimentTrends.WeeksAsync(sources, topicKey, weeks)` that applies the tenant's minimum group size. Engagement adds the source `community_room` (room messages) and `speak_up` (anonymous Speak Up posts, read only while the AI setting is on, with no author and no unit). The community page shows the trend to moderators and owners only.

### R3 decisions (smallest safe defaults, spec silent)
- "Long thread": 10 visible comments; "busy room": 20 visible messages.
- Rooms and mirroring are off in Workplace, on in Workplace Plus and Enterprise and Government.
- A Teams channel is named by its channel id; Teams answers 403 or 404 when the bot is not in that team, shown as the last failure.
- Stream lifetime 2 minutes, poll 2 seconds.

### R3 tests
- Rooms: owner opens, member writes, open reader reads, non-member of a closed community gets 404; stream delivers a new message and stops after access is lost; mentions told only to readers; keyword hold; 3 reports hide; moderator removal with reason; muted member refused; archived room read only; entitlement; cross-tenant.
- Mirroring: administrator only; refused for closed, department, confidential communities; Slack and Teams notices sent once per post, with and without excerpt; failure recorded; entitlement.
- Summaries: short thread refused; reader sees only visible comments in the prompt; anonymous post refused until the setting is on and then passed without author; gateway denial shown; entitlement.
- Sentiment trends: topic key saved; owner sees weeks above the minimum group size only; member refused; Speak Up source empty while the setting is off.
