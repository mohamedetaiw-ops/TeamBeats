# Implementation Plan: Employee Directory, Profiles and Org Chart

**Spec:** `specs/010-people-directory/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0013, spec 003 plan (directory sync), module map (`People`)

## Summary
A `People` module owns profiles, visibility policy and the reporting line. Directory sync in `Identity` reads the extra Graph attributes (`manager`, `jobTitle`, `department`, `businessPhones`, `mobilePhone`, `officeLocation`, `employeeHireDate`, `preferredLanguage`) and publishes them in its sync events; `People` consumes them. Other modules use `People.Contracts`: `IReportingLine` (manager of, chain, reports) and `IProfiles` (visible fields for a viewer).

## Data model (schema `people`)
- `profile`: `tenant_id`, `user_id`, `fields jsonb` (value and source per field), `location`, `hire_date`, `photo_key`, `version`. Photos: `photo_version`, `photo_source` (own or directory), `directory_photo_tag` and `directory_photo_checked_at` (T012).
- `reporting_line`: `user_id`, `manager_user_id`, `from`, `to` (history).
- `field_policy`: field key, source, visibility, names per locale for tenant fields.

## Visibility
`IProfiles.ViewAsync(viewer, userIds)` returns only fields the viewer may see; every endpoint and the search document builder use it. Policy cached per tenant with 60 second effect.

## Search
Until planned spec 012 exists, `GET /api/v1/people?q=` uses PostgreSQL full text and trigram matching on visible fields (pg_trgm is in PostgreSQL's contrib set, no new service). Spec 012 later indexes people through `ProfileUpdated`.

## API (v1)
`GET /api/v1/people?q=`, `GET /api/v1/people/{id}`, `GET /api/v1/people/{id}/org-chart`, `GET /api/v1/me/team?indirect=`, `GET /api/v1/people/newcomers`, `PUT /api/v1/me/profile`, `PUT /api/v1/me/photo`, `GET/PUT /api/v1/people/policy`.

## Tests
Integration per user story (US1 1 to 4, US2 1 to 5, US3 1 to 4, US4 1 and 2); visibility in API and search; cross-tenant search; cycle handling in sync; rate limit; performance at 50,000 members. Acceptance (e2e): search, profile, org chart and My team in English and Arabic with axe.

## R2 plan (2026-11-26)
Out of office from leave (US7 scenario 1) is in place through `ILeave.AwayAsync` (spec 022 slice 1), HR fields (US7 scenario 2) through spec 019 `HrProfiles`, and recognition on the profile (US8 scenario 4) through the spec 023 recognitions endpoint. What remains:

**Slice 1, skills (US6, FR-018, FR-019, FR-031):** capability `people.skills` (Entitlements migration, on for Workplace Plus and above). People migration adds `skill_setting` (tenant row: enabled, free text allowed, turned off at, version), `skill` (names per locale, status `active` or `suggested`), `person_skill` (user, skill, proficiency) and `endorsement` (skill owner, skill, endorser, hidden). Endpoints `GET/PUT /api/v1/people/skills` (list and settings, If-Match), `POST /api/v1/people/skills/{id}:merge`, `GET/PUT /api/v1/me/skills`, `GET /api/v1/people/{id}/skills`, `PUT/DELETE /api/v1/people/{id}/skills/{skillId}/endorsement`, `POST /api/v1/me/skills/{skillId}/endorsements/{endorserId}:hide|:show`, `GET /api/v1/people/experts?skillId=&q=`. Skills follow the visibility of the person's name (everyone who may see the person). An endorsement notifies the owner through the `people.skill.endorsed` template. The People maintenance job deletes skill data 90 days after the setting was turned off. Web: skills section on the profile with endorse, skills editor on the own profile, skill list in people settings, Find an expert page.

**Slice 2, completeness and relationships (US8, FR-028 to FR-030):** completeness from the own profile view (employee-editable fields, photo, skills) with a dismissal kept in `profile.completeness_dismissed_at`. `assistant` table (person, assistant, set by, at) with `PUT /api/v1/people/{id}/assistants`, audited. The delegate comes from a new spec 016 contract `IDelegations.ActiveAsync(userId)` returning the delegate and the last day only. Both show on the profile.

Decisions: skills visible to everyone who may see the person (smallest safe default, the spec gives skills no own visibility rule); suggested free-text skills are visible only to their owner and administrators until approved; endorsement notifications at most one per endorser per skill.

Tests: declare, endorse, hide, rank, visibility and cross-tenant, package and setting off hides everything, purge after 90 days, merge; completeness counts and dismissal; assistants by administrators only; delegation shown only inside its range; e2e for profile skills, Find an expert and completeness in English and Arabic with axe.

## R3 plan (2026-11-26)
Teams presence on the profile (US10, FR-032) is in place through spec 018 R3: `GET /api/v1/people/{id}/presence` with the free and busy visibility rule and `PresenceStatus` on the profile page. What remains is skill suggestions (US9, FR-033 to FR-035).

**Slice, skill suggestions:** People migration adds `suggestion_consent` (user, on, changed at) and `skill_suggestion` (user, skill, state `open` or `dismissed`, the cited items as JSON of source, id, title and link, created at), with forced RLS. A new contract `IAuthoredWorkSource` in People.Contracts is implemented by Content (published items whose published version the person wrote, title and summary or body text) and Engagement (the person's published posts in communities that are not Confidential; anonymous posts live in their own table and are never read). Endpoints: `GET/PUT /api/v1/me/skills/suggestions` (consent and open suggestions), `POST /api/v1/me/skills/suggestions:run`, `POST /api/v1/me/skills/suggestions/{skillId}:accept` with a proficiency, and `:dismiss`. A run needs `people.skills` on, `ai.assistant` and consent; it reads the newest 50 items of the last 12 months, sends them with the active skill list to the AI Gateway (use case summarize, Internal, JSON schema `expertise` of skill ids with the item numbers), keeps only ids from the list that the person has not declared or dismissed, at most five, and replaces the open suggestions. At most three runs per person per day (counted from the audit of `people.skills_suggested`). Turning consent off deletes open suggestions; dismissed ones stay so they are not offered again. The fake model answers the `expertise` schema with each listed skill whose English name appears in an item.

Decisions: suggestions read only Content and Engagement items, the two places people publish under their name; documents and knowledge articles are left for later. A run is started by the person, never in the background, so nothing is read without them asking.

Tests: consent off reads nothing and refuses a run; a run suggests only listed skills found in the person's own published items, cites them, leaves out declared and dismissed skills and Confidential items; accept declares the skill; dismiss keeps it away; consent off deletes open suggestions; the day limit; package without `ai.assistant` or skills off refused; another person never sees them; cross-tenant. e2e for turning on, running, accepting and dismissing in English and Arabic with axe.
