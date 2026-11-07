# Feature Specification: Communities and Engagement

| Field | Value |
|---|---|
| Spec ID | 023-communities-and-engagement |
| Area | experience |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R2 (communities and discussion channels, posts with comments, reactions, mentions and hashtags, Speak Up with anonymous option, polls, anonymous surveys with results thresholds, recognition and thank-you shout-outs, custom badges, points and leaderboard, team and star of the period, celebrations for birthdays and work anniversaries, photo galleries, moderation), R3 (discussion rooms with live chat, Teams and Slack channel mirroring, AI summaries of long threads, sentiment trends) |
| Last updated | 2026-11-07 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | EXP-007, ENG-001 to ENG-026 (new) |
| Use cases | UC-03 |
| Depends on | 002 (org units for department channels), 004 (authorization), 005 (entitlements), 009 (comments, reactions and moderation patterns, publishing), 010 (people, birthdays, work anniversaries), 011 (images and files), 012 (search), 015 (notifications), 021 (analytics), 026 (content safety, anonymity controls) |
| Used by | 008 (home widgets: discussions, celebrations, recognition, polls), 029 (project space discussions), 021 (engagement analytics) |

## 1. Problem and outcome

**Problem:** Intranets that only broadcast news lose employees after the first week. The current intranet has discussion channels, Speak Up, polls, a gallery, team and star of the period, celebrations and banking points, but each is its own screen with its own data, the home widget and the list page disagree, anonymous feedback is not really anonymous to administrators, and nobody moderates.

**Outcome:** One `Engagement` module holds communities, posts, polls, surveys, recognition and celebrations with one set of rules for membership, moderation, notifications and search. Home widgets and full pages read the same data. Anonymous Speak Up and surveys are anonymous by design: the identity is not stored with the response, and results below a threshold are never shown. Recognition and points are configurable per tenant and never tied to pay.

## 2. Scope

**In scope:**
- Communities: open, approval-required and closed; department communities linked to org units (members follow the unit); owners and moderators.
- Channels inside a community; posts with rich text (same editor as 009), images and files (011), comments, threaded replies one level deep, reactions, @mentions, #hashtags, pinning, following.
- Speak Up: a channel type where posts go to a review queue first, with an anonymous option the tenant can turn on.
- Polls: single and multiple choice, end date, anonymous or named, results shown after voting or after close.
- Surveys: multi-question, anonymous or named, audience targeting, minimum response threshold before results show (default 5), export of aggregated results.
- Recognition: thank-you shout-outs to people or teams, linked to company values, custom badges, visible on profiles (010) and a recognition wall.
- Points and leaderboard: points for configured actions (recognition received, posts, poll votes), leaderboard per period and org unit, can be turned off.
- Team and star of the period: nomination, selection by an approver, announcement.
- Celebrations: birthdays (opt-in, day and month only), work anniversaries, new joiners; a celebrations widget and greetings.
- Galleries: albums of images and videos from 011 storage, with moderation.
- Moderation: report a post or comment, moderator queue, hide, remove with reason, mute a member for a time, keyword filter, content safety scan through 026 for images.

**Out of scope:**
- Real-time chat and discussion rooms (R3), video calls.
- Mirroring to Teams or Slack channels (R3).
- Rewards redemption or any monetary value for points.
- External communities with customers.

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Join open communities, post, comment, react, vote, answer surveys, recognize colleagues | `engagement.participate` in scope |
| Community owner | Manage settings, members, channels and moderators of their community | Owner relation on the community |
| Moderator | Review reports and the Speak Up queue, hide or remove content, mute members | Moderator relation, or `engagement.moderate` in org scope |
| Engagement administrator | Create communities of any type, configure points, badges, values, celebrations, survey thresholds, keyword filters | `engagement.manage` at tenant scope |
| Survey author | Create and publish surveys and polls to an audience in scope | `engagement.survey_publish` in org scope |
| Recognition approver | Select team and star of the period | `engagement.recognition_approve` in org scope |

## 4. User scenarios

### User Story 1: Join a community and take part (Priority: P1, R2)

Laila joins "Photography club" and the Finance department community she is in automatically.

**Why this priority:** communities are the base of every other story.

**Independent test:** create an open, an approval-required and a department community; check join, auto membership from the org unit, posting, comment, reaction, mention notification and search.

**Acceptance scenarios:**
1. **Given** an open community, **When** Laila joins, **Then** she can read and post at once; for approval-required, a request goes to the owner; closed communities are invisible to non-members, including in search.
2. **Given** a department community linked to an org unit, **Then** membership follows the unit (spec 002) and changes when people move.
3. **Given** a post with @Omar and #budget, **Then** Omar is notified (015) according to his preferences, and the hashtag page lists posts the viewer can see.
4. **Given** a comment on a post Laila follows, **Then** she is notified once per thread per hour at most, grouped.
5. **Given** a post, **Then** it is searchable (012) only by people who can read its community.

### User Story 2: Speak up safely (Priority: P1, R2)

Omar wants to raise a safety concern without his name.

**Acceptance scenarios:**
1. **Given** a Speak Up channel with anonymity allowed, **When** Omar posts anonymously, **Then** the post is stored without his identity, no administrator, moderator or audit log can link it to him, and the screen tells him this plainly before he posts.
2. **Given** an anonymous post, **Then** it goes to the review queue; a moderator publishes it, answers it, or declines it with a reason shown to the anonymous author through a one-time reply link.
3. **Given** anonymous posting, **Then** rate limits use a keyed hash that cannot be reversed to the user, rotated daily.
4. **Given** an anonymous post containing a person's name and an accusation, **Then** the moderator can edit it before publishing, with "edited by moderator" shown.

### User Story 3: Run a poll and a survey (Priority: P1, R2)

Mona runs a lunch poll and an anonymous engagement survey for Acme Gulf.

**Acceptance scenarios:**
1. **Given** a poll with an end date, **When** employees vote, **Then** each person votes once (or changes their vote until close if allowed), and results show per the poll's setting.
2. **Given** an anonymous survey with threshold 5, **When** fewer than 5 people in a group answered, **Then** results for that group are hidden, including in filters and exports, so no small group can be singled out.
3. **Given** an anonymous survey, **Then** responses are stored without user identity; only "has responded" is kept separately to stop double answers and send reminders, and it is deleted when the survey closes.
4. **Given** a named survey, **Then** responses show names only to the author and people with `engagement.survey_publish` on that audience.
5. **Given** results, **Then** the author can export aggregated results as CSV; free-text answers export without identity for anonymous surveys.

### User Story 4: Recognize a colleague (Priority: P1, R2)

Sara thanks Ahmed for help with month-end closing, linked to the value "Teamwork".

**Acceptance scenarios:**
1. **Given** the shout-out, **Then** it appears on the recognition wall, on Ahmed's profile (010) if he allows, and Ahmed and his manager are notified.
2. **Given** points are on, **Then** Ahmed receives the configured points; a person cannot recognize themselves; at most the tenant's daily limit of shout-outs per giver (default 5) count for points.
3. **Given** a custom badge "Month-end hero", **When** Sara gives it, **Then** only badges allowed for givers in her scope are offered.
4. **Given** points are off for the tenant, **Then** no points, leaderboards or point counts appear anywhere.

### User Story 5: Choose the team and star of the period (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a period opened by the engagement administrator, **When** employees nominate with a reason, **Then** recognition approvers in scope see nominations and select winners.
2. **Given** winners, **Then** an announcement is drafted as content (009) for the communicator to publish.

### User Story 6: Celebrate people (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** Laila opted in to birthday celebrations, **When** her birthday comes, **Then** the celebrations widget shows her name and colleagues can send a greeting; the year is never shown or stored.
2. **Given** an employee who did not opt in, **Then** nothing about their birthday appears anywhere.
3. **Given** work anniversaries and new joiners from 010 and 019, **Then** they appear on the widget according to the tenant's settings, filtered to the viewer's org unit by default.

### User Story 7: Share photos in a gallery (Priority: P3, R2)

**Acceptance scenarios:**
1. **Given** an album in a community, **When** members add photos, **Then** images are scanned (malware and content safety, spec 026), resized, and shown after moderation if the album requires it.
2. **Given** a person tagged in a photo, **Then** they are notified and can remove the tag.

### User Story 8: Moderate (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** a reported post, **Then** it goes to the community moderators' queue with the reason; after 3 reports from different people it is hidden until reviewed.
2. **Given** a keyword on the tenant's filter list, **When** someone posts it, **Then** the post is held for review and the author is told.
3. **Given** a removed post, **Then** the author sees the reason, the action is audited, and the post is removed from search within 1 minute.
4. **Given** a muted member, **Then** they can read but not post or comment in that community until the mute ends.

### Edge cases and exception flows
- Author leaves the company: posts stay with the name shown as a former employee; anonymous posts are unaffected.
- Department community whose unit is deleted: the community is archived, readable by former members.
- Poll edited after votes: options can only be added, not changed or removed.
- Survey audience shrinks below threshold: results stay hidden.
- Mention of someone without access to the community: no notification, and the author is warned.

## 5. Functional requirements

- **ENG-001 / FR-001**: Communities MUST support open, approval-required and closed types; closed communities and their content MUST be invisible to non-members in every surface including search. *(EXP-007)*
- **ENG-002 / FR-002**: Department communities MUST derive membership from org units and follow spec 002 changes.
- **ENG-003 / FR-003**: Posts MUST support rich text, images, files, comments with one reply level, reactions, mentions, hashtags, pinning and following.
- **ENG-004 / FR-004**: Mentions and replies MUST notify through spec 015 with grouping, and MUST NOT notify people who cannot read the content.
- **ENG-005 / FR-005**: Anonymous Speak Up posts and anonymous survey responses MUST be stored without any field that links them to the user, including in audit, logs and telemetry.
- **ENG-006 / FR-006**: Anonymous rate limiting MUST use a daily rotated keyed hash.
- **ENG-007 / FR-007**: Speak Up posts MUST pass a review queue before publication.
- **ENG-008 / FR-008**: Polls MUST allow one vote per person, optional change until close, and results visibility settings.
- **ENG-009 / FR-009**: Survey results for any group below the threshold (default 5, minimum 3) MUST be hidden in views, filters and exports.
- **ENG-010 / FR-010**: "Has responded" markers for anonymous surveys MUST be stored apart from responses and deleted at close.
- **ENG-011 / FR-011**: Recognition MUST support people and teams, company values and custom badges, and MUST refuse self recognition.
- **ENG-012 / FR-012**: Points MUST be configurable per action with daily caps, MUST be switchable off for the tenant, and MUST NOT have monetary value in the product.
- **ENG-013 / FR-013**: Leaderboards MUST be per period and org unit and MUST hide people who opted out.
- **ENG-014 / FR-014**: Team and star of the period MUST support nominations, approver selection and an announcement draft through 009.
- **ENG-015 / FR-015**: Birthdays MUST be opt-in and store day and month only.
- **ENG-016 / FR-016**: Celebrations MUST read joiners and work anniversaries from 010 and filter by the viewer's unit by default.
- **ENG-017 / FR-017**: Gallery uploads MUST be scanned for malware and content safety before display.
- **ENG-018 / FR-018**: Reports MUST queue to moderators; 3 distinct reports MUST hide content until review.
- **ENG-019 / FR-019**: Keyword filters MUST hold matching posts for review.
- **ENG-020 / FR-020**: Removal MUST record a reason, notify the author and remove the item from search within 1 minute.
- **ENG-021 / FR-021**: Mutes MUST be time-bound and per community.
- **ENG-022 / FR-022**: Home widgets and full pages MUST read the same data through the same queries.
- **ENG-023 / FR-023**: Engagement content MUST be indexed by 012 with the community's access rules.
- **ENG-024 / FR-024**: Engagement events MUST feed 021 as counts without identities for anonymous items.
- **ENG-025 / FR-025**: Community owners MUST NOT see anonymous identities; there is no override role.
- **ENG-026 / FR-026**: All user-facing text MUST use resource keys and work in RTL.

## 6. Business and validation rules
- Post length up to 10,000 characters; up to 10 images per post; comment length up to 2,000.
- Survey threshold default 5, tenant range 3 to 50.
- Default points: shout-out received 10, post 2, poll vote 1; daily cap per person 50.
- Report hide threshold 3 distinct reporters.

## 7. Tenant and organization scope
- Tenant-owned data: communities, channels, posts, comments, reactions, polls, surveys, responses, recognition, badges, points, celebrations settings, galleries.
- Organization scoping: communities and surveys can target org units; moderators and approvers act within their scope.
- Cross-tenant denial cases: mentions, hashtags and search never cross tenants; anonymous hash keys are per tenant.
- Sibling, parent and child cases: a survey for Acme Gulf excludes Acme Africa; a parent unit survey shows child results only above threshold.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Posts, comments, reactions | Product | Internal | Life of community, tenant retention policy | Yes |
| Anonymous posts and responses | Product | Internal | Survey or channel retention | No by design |
| Named survey responses | Product | Confidential | 2 years default | Yes |
| Recognition and points | Product | Internal | 3 years | Yes |
| Birthday day and month | Product | Internal | Until opt-out | Yes |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `engagement.communities` | on | on | on |
| `engagement.speak_up` | off | on | on |
| `engagement.surveys` | polls only | on | on |
| `engagement.recognition` | on | on | on |
| `engagement.points` | off | on | on |
| `engagement.galleries` | on | on | on |

## 10. UX, localization and accessibility
- Communities directory, community page with channels, post composer shared with 009, recognition wall, survey builder with preview, moderator queue.
- Widgets for 008: latest discussions, celebrations, recognition, active poll.
- Arabic and English, RTL, keyboard operable; reactions have text labels for screen readers.

## 11. AI behavior
R2: none. R3: thread summaries and sentiment trends through 013 and 014, never on anonymous content without the tenant's explicit setting, never identifying anonymous authors.

## 12. Integrations and events
- Publishes `PostPublished`, `PostRemoved`, `CommentAdded`, `RecognitionGiven`, `SurveyClosed`, `PollClosed`.
- Consumes `OrgUnitMembershipChanged` (002), `EmployeeHired` (019), profile changes (010).

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| engagement.community_created / archived | audit | community, type, actor |
| engagement.content_removed / hidden | audit | item, reason, actor |
| engagement.member_muted | audit | community, member, until, actor |
| engagement.survey_published / closed | audit | survey, audience, actor |
| engagement.points_config_changed | audit | config version, actor |
| engagement.activity | telemetry | type, community (no content, no identity for anonymous) |

## 14. Security and abuse cases
- De-anonymization through timing or metadata: anonymous items store a day-level time only, no IP, no device.
- Points gaming: daily caps, no self recognition, mutual recognition loops flagged to administrators.
- Harassment: reporting, auto hide, mutes, keyword filters.
- Malicious images: malware and content safety scan.

## 15. Non-functional requirements
- Community feed first page under 800 ms p95.
- Survey of 10,000 respondents aggregates in under 10 seconds.

## 16. Success criteria
- **SC-001**: 40% of employees take part (post, comment, react, vote or recognize) monthly in pilot tenants.
- **SC-002**: No anonymous item can be linked to its author in a security review.

## 17. Open questions
- [NEEDS CLARIFICATION: can managers see survey results for their own team?] Default: yes, above threshold, when the author shares results with managers.
- [NEEDS CLARIFICATION: are leaderboards on by default?] Default: off; the administrator turns them on.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| EXP-007 | ENG-001 to ENG-026, US1 to US8 |
| UC-03 | US1, US4 |
