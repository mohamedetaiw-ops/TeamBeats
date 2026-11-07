# Feature Specification: Employee Directory, Profiles and Org Chart

| Field | Value |
|---|---|
| Spec ID | 010-people-directory |
| Area | experience |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (profiles from the directory with source ownership and visibility, reporting line, people search, profile page, org chart, My team, newcomers, the people component), R2 (skills and expertise, out of office from leave, HR sync fields, badges from recognition, profile completeness prompts) |
| Last updated | 2026-11-07 |
| Requirement IDs | EXP-005, EXP-006, PPL-001 to PPL-014 (new) |
| Use cases | UC-05 |
| Depends on | 002 (hierarchy, memberships), 003 (directory sync, user lookup), 004 (authorization), 005, 006, 007, 008 (shell), 012 (people in search), 018 (free and busy on profiles), 032 (component manifests) |
| Used by | 009 (manager lists, mentions), 016 (line manager approval, Team requests), 018 (who may see availability), 021 (location breakdowns), 022 (team calendar, R2), 025 (manager escalation), 027 (agents read profiles as the user) |

## 1. Problem and outcome

**Problem:** Employees cannot find colleagues, see who reports to whom, or know who is new. Several approved specs already depend on a reporting line (line manager approval in 016, manager escalation in 009 and 025) and on knowing who may see a profile (018), but nothing in the product holds a profile or a reporting line yet. Directory sync (spec 003) creates users and memberships but does not keep job title, phone, office or manager.

**Outcome:** Every employee has a profile filled from the directory, with each field owned by its source and shown according to a visibility policy. People search, the profile page, the org chart, My team and the newcomers list work for every tenant. The reporting line comes from the directory `manager` attribute and is the one source of "who is my manager" for the rest of the product.

## 2. Scope

**In scope:**
- Profile fields: display name per locale, job title, department text, office location, work phone, mobile phone, photo, email, preferred language, hire date, manager, and up to 10 tenant-defined text fields.
- Source ownership per field: directory (read-only in TeamBeats), employee-editable, or administrator-managed.
- Visibility policy per field: everyone in the tenant, same org subtree, managers and HR, nobody but the person and administrators.
- Reporting line from the directory `manager` attribute, with history of changes.
- People search by name, job title, department and phone extension, in Arabic and English, with permission and visibility filtering.
- Profile page, org chart (manager, peers, direct reports, navigable by keyboard), My team, newcomers (joined in the last 30 days by default).
- People component for the page builder (spec 032): a list of people by unit, newcomers or a named set.
- Location as a profile attribute from the directory (`officeLocation` or a mapped attribute), usable as a targeting and breakdown dimension by specs 009, 021, 022 and 025.

**Out of scope:**
- Skills, expertise and endorsements (R2).
- Out of office status from leave (spec 022, R2) and Teams presence (spec 018, R3).
- HR system fields (spec 019, R2).
- Writing profile changes back to the directory.

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Search people, open profiles and the org chart within visibility rules, edit their own employee-editable fields | Active tenant membership; `people.view` (in the Employee role) |
| Line manager | See My team and the manager-visible fields of their reports | Reporting line |
| HR officer | See HR-visible fields in their scope | `people.hr_view` at org scope |
| Tenant administrator | Set field sources, visibility policy, tenant fields, location attribute mapping | `people.manage` at tenant scope |

## 4. User scenarios

### User Story 1: Find a colleague (Priority: P1, R1)

Sara needs someone in Finance who handles travel claims. She types "travel" and "finance" in people search and sees two people with their job titles and units. She opens one profile and calls the extension.

**Independent test:** sync users with job titles and departments, search by name and title in both languages, check filtering by visibility and org boundary.

**Acceptance scenarios:**
1. **Given** synced users, **When** an employee searches by part of a name, job title, department or extension, **Then** matching active members of the same tenant appear with photo, name, title and unit, in the user's language where a localized name exists.
2. **Given** a field whose visibility is "same subtree", **When** a user outside that subtree sees the result or profile, **Then** that field is not returned by the API.
3. **Given** a disabled membership, **Then** the person does not appear in search, and an old profile link shows "no longer available".
4. **Given** a user of another tenant, **Then** they are never found, even by exact email.
5. **Given** a guest membership (spec 026, R2), **Then** guests see no directory unless a later spec allows it.

### User Story 2: Reporting line and org chart (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** the directory `manager` attribute, **When** sync runs, **Then** each person's manager is set to the member with that directory identity, and a change is recorded with its date and published as an event.
2. **Given** a manager who is not a member of the tenant (or is disabled), **Then** the person has no manager in TeamBeats and the profile shows none; specs that need a manager use their own fallback.
3. **Given** a profile, **When** the user opens the org chart, **Then** they see the manager chain up to the top, peers and direct reports, can move through it by keyboard, and it mirrors in RTL.
4. **Given** a manager, **When** they open My team, **Then** they see direct reports and, on request, indirect reports, with the manager-visible fields.
5. **Given** a cycle in the directory data (A manages B, B manages A), **Then** sync keeps the previous line for the affected people and reports the cycle to the administrator.

### User Story 3: Own profile and visibility policy (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** the tenant marks mobile phone as employee-editable and visible to "managers and HR", **When** an employee edits it, **Then** it saves, and only their managers, HR officers in scope, administrators and the employee see it.
2. **Given** a directory-owned field, **When** the employee tries to edit it, **Then** it is read-only with the source named.
3. **Given** a photo upload, **Then** it is checked for type and size, stored under the tenant's path, and shown once processed.
4. **Given** a policy change that narrows visibility, **Then** it applies to API answers within 60 seconds and to search results within the synchronization objective of spec 012.

### User Story 4: Newcomers and the people component (Priority: P2, R1)

**Acceptance scenarios:**
1. **Given** members with a hire date (or a first sync date when the directory has none) in the last 30 days, **When** the newcomers component renders, **Then** it lists those the viewer may see, newest first.
2. **Given** a people component set to a unit, **Then** it lists active members of that unit (with or without subtree) the viewer may see, in every compatible region and width, in both directions.

### Edge cases
- Two people with the same display name: results show title and unit to tell them apart.
- A person in two units: the primary membership (spec 002) decides the unit shown; the profile lists all units.
- Directory sends an empty manager: the line is cleared and recorded.
- A tenant without directory sync (dev sign-in or manual users): administrators can set manager and fields by hand.

## 5. Functional requirements

- **PPL-001 / FR-001**: The system MUST keep one profile per tenant membership with the fields of section 2. *(EXP-005)*
- **PPL-002 / FR-002**: Each field MUST have a source (directory, employee, administrator) and fields owned by the directory MUST be read-only in TeamBeats. *(EXP-006)*
- **PPL-003 / FR-003**: Each field MUST have a visibility rule, enforced in the API and in search, never only in the UI. *(EXP-006, constitution III)*
- **PPL-004 / FR-004**: The reporting line MUST come from the directory `manager` attribute, with administrator override only for tenants without directory sync, and changes MUST be recorded with dates.
- **PPL-005 / FR-005**: The system MUST offer a reporting line contract (manager of, manager chain to level N, direct and indirect reports) for other modules.
- **PPL-006 / FR-006**: People search MUST filter by tenant, active membership and field visibility, and MUST match Arabic and English names and job titles. *(UC-05)*
- **PPL-007 / FR-007**: The org chart MUST show the manager chain, peers and reports, be operable by keyboard and readable by screen readers, and mirror in RTL.
- **PPL-008 / FR-008**: My team MUST list direct reports and optionally indirect reports.
- **PPL-009 / FR-009**: Newcomers MUST list members who joined within a tenant-set period (default 30 days).
- **PPL-010 / FR-010**: The people component MUST be registered through a spec 032 manifest and show only people the viewer may see on every render.
- **PPL-011 / FR-011**: Location MUST be a profile attribute mapped from a directory attribute and exposed as a dimension for targeting (009), breakdowns (021, 025) and calendars (022).
- **PPL-012 / FR-012**: Profile changes MUST publish `ProfileUpdated` and `ReportingLineChanged` with IDs and changed field names, not values.
- **PPL-013 / FR-013**: Photos MUST be type and size checked and stored under the tenant's object path.
- **PPL-014 / FR-014**: Who may see a person's free and busy (spec 018 US4) MUST be decided by the same profile visibility rule.

## 6. Business and validation rules
- Default visibility: name, photo, title, unit, email, work phone and office location to everyone in the tenant; mobile phone to managers and HR; hire date to managers and HR.
- Tenant-defined fields: at most 10, text only in R1, each with a name per locale.
- A person with no manager is valid.

## 7. Tenant and organization scope
- Tenant-owned data: profiles, reporting lines, visibility policy.
- Organization scoping: "same subtree" visibility uses the viewer's memberships; HR visibility uses `people.hr_view` scope.
- Cross-tenant denial: search, profile, org chart and reporting line queries never return another tenant's people.
- Sibling, parent and child cases: a "same subtree" field of an Acme Gulf person is hidden from Acme Africa viewers.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Profile | Directory for synced fields, product for others | Internal (mobile phone Confidential) | Membership life plus 30 days | Yes |
| Reporting line history | Product (from directory) | Internal | 3 years | Yes |
| Photo | Directory or product object storage | Internal | Membership life | Yes |

## 9. Package entitlements

| Capability key | Behavior |
|---|---|
| `people.directory` | All packages: directory, profiles, org chart, My team, newcomers |
| `people.custom_fields` | Workplace Plus and above: tenant-defined fields |

## 10. UX, localization and accessibility
- Names show in the user's locale where a localized name exists, else the directory name.
- The org chart has a list view as an alternative to the tree.
- All labels are resource keys; layouts work RTL.

## 11. AI behavior
Agents read profiles only through this module's contract as the user, with the same visibility rules (spec 027). No profile data is sent to a model unless the user's question needs it.

## 12. Integrations and events
- Consumes `UserProvisioned`, `UserDeprovisioned` and directory sync changes (spec 003) with the `manager`, job title, department, phone and office attributes added to the sync.
- Publishes `ProfileUpdated`, `ReportingLineChanged`.

## 13. Audit

| Event | Type | Key fields |
|---|---|---|
| people.visibility_changed | audit | field, old and new rule, actor |
| people.field_source_changed | audit | field, old and new source, actor |
| people.manager_set_manually | audit | person, manager, actor |
| people.profile_updated | audit | person, field names (no values) |

## 14. Security and abuse cases
- Enumerating a restricted unit through search: results apply visibility, and counts never reveal hidden people.
- Scraping the directory: search is rate limited per user.
- Inferring a hidden field from sort order: sorting uses only fields visible to the viewer.

## 15. Non-functional requirements
- People search p95 under 300 ms at 50,000 members.
- Org chart of a manager with 200 reports renders under 1 second.

## 16. Success criteria
- **SC-001**: An employee finds a colleague by name or title in under 15 seconds in usability tests.
- **SC-002**: Every approval that needs a line manager in specs 009, 016 and 025 resolves from this spec's reporting line in the pilot tenant.

## 17. Open questions
- [NEEDS CLARIFICATION: should employees be able to hide their own profile from search?] Default: no; tenants can narrow field visibility instead.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| EXP-005 | PPL-001, PPL-006 to PPL-010, US1, US2, US4 |
| EXP-006 | PPL-002, PPL-003, US3 |
| UC-05 | US1 |
