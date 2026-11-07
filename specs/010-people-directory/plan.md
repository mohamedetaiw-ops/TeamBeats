# Implementation Plan: Employee Directory, Profiles and Org Chart

**Spec:** `specs/010-people-directory/spec.md` (v0.1, Proposed) | **Date:** 2026-11-07
**Shared base:** ADR-0013, spec 003 plan (directory sync), module map (`People`)

## Summary
A `People` module owns profiles, visibility policy and the reporting line. Directory sync in `Identity` reads the extra Graph attributes (`manager`, `jobTitle`, `department`, `businessPhones`, `mobilePhone`, `officeLocation`, `employeeHireDate`, `preferredLanguage`) and publishes them in its sync events; `People` consumes them. Other modules use `People.Contracts`: `IReportingLine` (manager of, chain, reports) and `IProfiles` (visible fields for a viewer).

## Data model (schema `people`)
- `profile`: `tenant_id`, `user_id`, `fields jsonb` (value and source per field), `location`, `hire_date`, `photo_key`, `version`.
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
