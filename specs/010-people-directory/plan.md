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
