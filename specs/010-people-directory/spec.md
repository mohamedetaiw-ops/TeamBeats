# Feature Specification: Employee Directory, Profiles and Org Chart

| Field | Value |
|---|---|
| Spec ID | 010-people-directory |
| Area | experience |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (profiles from the directory with source ownership and visibility policy, reporting line, location attribute, people search, profile page, org chart, My team, newcomers, people component, contact actions), R2 (skills and expertise with endorsements, find an expert, out of office from leave, HR system fields through spec 019, badges from recognition in spec 023, profile completeness prompts, delegate and assistant relationships), R3 (Teams presence through spec 018, expertise suggested from authored content with consent) |
| Last updated | 2026-11-07 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | EXP-005, EXP-006, PPL-001 to PPL-022 (new) |
| Use cases | UC-05 |
| Depends on | 002 (hierarchy, memberships), 003 (directory sync, user lookup), 004 (authorization), 005 (entitlements), 006 (audit), 007 (localization), 008 (shell, quick navigation), 012 (people in search), 015 (notifications), 018 (free and busy), 019 (HR fields, R2), 022 (out of office, R2), 023 (badges, R2), 032 (component manifests) |
| Used by | 009 (manager lists, mentions, location targeting), 016 (line manager approval, Team requests, prefill), 018 (who may see availability), 021 (location breakdowns), 022 (team calendar), 025 (manager escalation), 026 (access reviews by manager), 027 (agents read profiles as the user) |

## 1. Problem and outcome

**Problem:** Employees cannot find colleagues, see who reports to whom, or know who is new. The old intranet's directory was a flat list copied from the HR system once a month, so job titles, phones and managers were often wrong, and everyone could see everyone's mobile number. Several approved specs depend on a reporting line (line manager approval in 016, manager escalation in 009 and 025, access reviews in 026) and on knowing who may see a profile (018), but nothing in the product holds a profile or a reporting line yet. Directory sync (spec 003) creates users and memberships but does not keep job title, phone, office or manager.

**Outcome:** Every employee has one profile per tenant membership, filled from the directory, with each field owned by one source and shown according to a visibility policy the tenant controls. People search, the profile page, the org chart, My team and the newcomers list work for every tenant in Arabic and English. The reporting line comes from the directory `manager` attribute and is the single source of "who is my manager" for the rest of the product. From R2, employees add skills and expertise so colleagues can find the right person, and profiles show out of office status from leave.

## 2. Scope

**In scope:**
- Profile fields: display name per locale, job title, department text, office location, work phone, mobile phone, photo, email, preferred language, time zone, hire date, manager, employee number (hidden by default), and up to 10 tenant-defined fields.
- Source ownership per field: directory (read-only in TeamBeats), HR system (read-only, R2 through spec 019), employee-editable, or administrator-managed.
- Visibility policy per field: everyone in the tenant, same org subtree, the person's managers and HR officers in scope, only the person and administrators.
- Reporting line from the directory `manager` attribute, with dates of every change, and a manual override for tenants without directory sync.
- Location as a profile attribute mapped from a directory attribute (`officeLocation` by default), used as a dimension for content targeting (009), breakdowns (021, 025) and calendars (022).
- People search by name, job title, department, phone extension and email, in Arabic and English, with visibility and permission filtering; quick navigation entry (spec 008).
- Profile page with contact actions (email, call, chat link where Teams is connected in R2, free and busy from 018), org chart (manager chain, peers, direct reports), My team (direct and indirect reports), newcomers.
- People component for the page builder (spec 032): people of a unit, newcomers, or a named set.
- Skills and expertise (R2): a tenant skill list, employee-declared skills with proficiency, endorsements by colleagues, and "find an expert" search.
- Out of office status (R2) from approved leave in spec 022, with dates shown according to the leave type's privacy rule.

**Out of scope:**
- Writing profile changes back to the directory or HR system. Changes to directory-owned data are made at the source.
- Performance, compensation or any HR record beyond the fields above (spec 019 and the HR system).
- Social graph features such as following people (spec 023 covers communities).
- Contractors and guests outside the tenant directory (guests in spec 026 have no directory access unless a later spec allows it).

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Employee | Search people, open profiles and the org chart within visibility rules, edit their own employee-editable fields and photo, add skills (R2) | Active tenant membership; `people.view` (held by the Employee role) |
| Line manager | See My team and the manager-visible fields of their direct and indirect reports | Reporting line (this spec) |
| HR officer | See HR-visible fields of people in their scope; correct administrator-managed fields | `people.hr_view` at org scope |
| Tenant administrator | Set field sources and visibility, tenant fields, location attribute mapping, newcomer period, skill list; set managers by hand when there is no directory sync | `people.manage` at tenant scope |
| Organization administrator | See and correct administrator-managed fields for their subtree | `people.manage` at org scope |
| Assistant and agents | Read profiles as the user through this module's contract | Spec 027 tool grant plus the user's own visibility |

## 4. User scenarios

### User Story 1: Find a colleague (Priority: P1, R1)

Sara needs someone in Finance who handles travel claims. She types "travel finance" in people search and sees two people with photos, job titles and units. She opens one profile, sees the person is busy until 11:00, and calls the extension.

**Why this priority:** the directory is the most used page of every intranet and pilot customers test it first.

**Independent test:** sync users with job titles, departments and phones; search by name, title and extension in both languages; check visibility filtering and the org boundary.

**Acceptance scenarios:**
1. **Given** synced users, **When** an employee searches by part of a name, job title, department, extension or email, **Then** matching active members of the same tenant appear with photo, name, title and unit, ranked by match quality, with the localized name where one exists.
2. **Given** an Arabic query such as "محمد", **Then** names match with normalization of letter variants and without diacritics; **Given** a transliterated query such as "Mohamed" or "Mohammed", **Then** people whose English name matches either spelling appear.
3. **Given** a field whose visibility is "same subtree", **When** a user outside that subtree sees the result or the profile, **Then** that field is not returned by the API and cannot be used to filter or sort.
4. **Given** a disabled membership, **Then** the person does not appear in search, and an old profile link shows "no longer available" without other details.
5. **Given** a user of another tenant, **Then** they are never found, even by exact email.
6. **Given** a search with more than 50 results, **Then** results are paged and filters by unit and location narrow them.

### User Story 2: Reporting line and org chart (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** the directory `manager` attribute, **When** sync runs, **Then** each person's manager is set to the member with that directory identity, the change is recorded with its date, and `ReportingLineChanged` is published.
2. **Given** a manager who is not an active member of the tenant, **Then** the person has no manager in TeamBeats, the profile shows none, and specs that need a manager use their own fallback (for example the step fallback in spec 016).
3. **Given** a profile, **When** the user opens the org chart, **Then** they see the manager chain to the top, peers and direct reports, can move through it by keyboard, and it mirrors in RTL; a list view gives the same information.
4. **Given** a manager, **When** they open My team, **Then** they see direct reports and, on request, indirect reports, with the manager-visible fields.
5. **Given** a cycle in the directory data (A manages B and B manages A), **Then** sync keeps the previous line for the affected people and reports the cycle to the tenant administrator.
6. **Given** a tenant without directory sync, **When** a tenant administrator sets a manager by hand, **Then** the line is saved with the same history and event, and the action is audited.

### User Story 3: Own profile and visibility policy (Priority: P1, R1)

Omar wants his mobile number visible only to his managers and HR. The tenant has made mobile phone employee-editable with that default.

**Acceptance scenarios:**
1. **Given** the tenant marks mobile phone as employee-editable and visible to "managers and HR", **When** an employee edits it, **Then** it saves, and only their managers, HR officers in scope, administrators and the employee see it.
2. **Given** a directory-owned field, **When** the employee tries to edit it, **Then** it is read-only and the source is named ("from your organization's directory").
3. **Given** a photo upload, **Then** it is checked for type (PNG, JPEG) and size (2 MB), stored under the tenant's object path, resized, and shown once processed; a directory photo is used when the employee has not uploaded one.
4. **Given** a policy change that narrows visibility, **Then** it applies to API answers within 60 seconds and to search results within the synchronization objective of spec 012.
5. **Given** an administrator sets a field to "only the person and administrators", **Then** it disappears from every other view, export and agent answer.

### User Story 4: Newcomers and the people component (Priority: P2, R1)

**Acceptance scenarios:**
1. **Given** members whose hire date (or first sync date when the directory has none) is within the tenant's newcomer period (default 30 days), **When** the newcomers component renders, **Then** it lists those the viewer may see, newest first, with photo, title and unit.
2. **Given** a people component set to a unit with subtree, **Then** it lists active members of that subtree the viewer may see, in every compatible region and width, in both directions.
3. **Given** a newcomer who opted out of the newcomers list (where the tenant allows opt-out), **Then** they do not appear in it but remain findable in search.

### User Story 5: Location as a shared dimension (Priority: P1, R1)

**Acceptance scenarios:**
1. **Given** the tenant maps `officeLocation` to location, **When** sync runs, **Then** each profile's location is set and the list of locations is available to targeting (009), compliance breakdowns (025) and analytics (021).
2. **Given** a location value that differs only in case or spacing ("Dubai HQ", "dubai hq"), **Then** it is treated as one location.
3. **Given** an administrator renames a location's display name per locale, **Then** the stored key stays the same and every dimension shows the new name.

### User Story 6: Skills and find an expert (Priority: P2, R2)

Layla needs someone who knows Arabic legal translation. She searches "legal translation" in Find an expert and sees three people who declared the skill, two of them endorsed by colleagues.

**Acceptance scenarios:**
1. **Given** a tenant skill list, **When** an employee adds skills with proficiency (basic, good, expert), **Then** they appear on the profile with endorsement counts.
2. **Given** a colleague endorses a skill, **Then** the owner is notified (spec 015) and can hide an endorsement.
3. **Given** Find an expert, **Then** results rank by proficiency and endorsements and respect profile visibility.
4. **Given** the tenant turns skills off, **Then** skill data is hidden everywhere and kept for 90 days before deletion.

### User Story 7: Out of office and HR fields (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** approved leave (spec 022), **Then** the profile shows "out of office until" with the return date, never the leave type unless the leave type's privacy rule allows it.
2. **Given** an HR system connection (spec 019), **Then** mapped HR fields fill the profile as HR-owned and read-only, and a field owned by both the directory and HR follows the tenant's source of truth setting.

### Edge cases and exception flows
- Two people with the same display name: results show title, unit and location to tell them apart.
- A person in two units: the primary membership (spec 002) decides the unit shown; the profile lists all units the viewer may see.
- The directory sends an empty manager: the line is cleared and recorded with the date.
- A manager leaves: their reports have no manager until the directory sets a new one; approvals use spec 016 fallbacks.
- Directory unavailable: profiles keep their last synced values with the sync time; nothing is cleared.
- Very large teams (a manager with 2,000 reports): My team pages results; the org chart shows a count with a link to the list.
- A field changes source (for example from employee-editable to directory): the employee's value is kept in history and replaced at the next sync.

## 5. Functional requirements

- **PPL-001 / FR-001**: The system MUST keep one profile per tenant membership with the fields of section 2. *(EXP-005)*
- **PPL-002 / FR-002**: Each field MUST have exactly one source (directory, HR system, employee, administrator); fields owned by an external source MUST be read-only in TeamBeats. *(EXP-006)*
- **PPL-003 / FR-003**: Each field MUST have a visibility rule enforced in the API, in search, in exports and in agent tools, never only in the UI. *(EXP-006, constitution III)*
- **PPL-004 / FR-004**: The reporting line MUST come from the directory `manager` attribute; a manual override MUST be available only for tenants without directory sync; every change MUST be recorded with dates.
- **PPL-005 / FR-005**: The system MUST offer a reporting line contract (manager of, manager chain to level N, direct reports, all reports, is manager of) for other modules.
- **PPL-006 / FR-006**: People search MUST filter by tenant, active membership and field visibility, and MUST match Arabic and English names with normalization and common transliterations. *(UC-05)*
- **PPL-007 / FR-007**: The org chart MUST show the manager chain, peers and reports, be operable by keyboard and readable by screen readers, offer a list view, and mirror in RTL. *(EXP-008)*
- **PPL-008 / FR-008**: My team MUST list direct reports and optionally indirect reports, paged.
- **PPL-009 / FR-009**: Newcomers MUST list members who joined within a tenant-set period (default 30 days), with an opt-out the tenant can allow.
- **PPL-010 / FR-010**: The people component MUST be registered through a spec 032 manifest and show only people the viewer may see on every render.
- **PPL-011 / FR-011**: Location MUST be a profile attribute mapped from a directory attribute, normalized to one key per location with display names per locale, and exposed as a dimension for targeting (009), breakdowns (021, 025) and calendars (022).
- **PPL-012 / FR-012**: Profile changes MUST publish `ProfileUpdated` and `ReportingLineChanged` with IDs and changed field names, never values.
- **PPL-013 / FR-013**: Photos MUST be type and size checked, resized, and stored under the tenant's object path.
- **PPL-014 / FR-014**: Who may see a person's free and busy (spec 018 US4) MUST be decided by the profile visibility rule for the person.
- **PPL-015 / FR-015**: Contact actions MUST use only fields the viewer may see.
- **PPL-016 / FR-016**: Tenant-defined fields MUST have a name per locale, a source and a visibility rule; at most 10 in R1, text only.
- **PPL-017 / FR-017**: People search MUST be rate limited per user to prevent bulk scraping.
- **PPL-018 / FR-018**: *(R2)* Employees MUST be able to declare skills from the tenant list with proficiency, and colleagues MUST be able to endorse them.
- **PPL-019 / FR-019**: *(R2)* Find an expert MUST rank by declared proficiency and endorsements within visibility rules.
- **PPL-020 / FR-020**: *(R2)* Out of office status MUST come from approved leave (spec 022) and show only what the leave type's privacy rule allows.
- **PPL-021 / FR-021**: *(R2)* HR-owned fields MUST come from spec 019 with a tenant source of truth setting per field shared by the directory and HR.
- **PPL-022 / FR-022**: Profiles MUST keep their last synced values when the directory is unavailable, with the sync time shown to administrators.
- **FR-023**: The unit filter MUST make every unit distinguishable: units are listed as a tree with indentation, or with the parent's name when two units share a name ("Operations, Demo Services"). Acceptance: no two entries of the unit filter read the same.
- **FR-024**: While a search or filter is active, the People page MUST show only the results; the New joiners section shows only without a search. A person card with no title or unit shows "Title not set" in the secondary text style instead of an empty line. Acceptance: searching People hides New joiners; a person with no title shows the fallback.

## 6. Business and validation rules
- Default visibility: name, photo, title, unit, email, work phone, office location and preferred language to everyone in the tenant; mobile phone and hire date to managers and HR; employee number to HR and administrators.
- Phone numbers are stored as given by the source and displayed as entered; extensions are matched as digits.
- A person with no manager is valid.
- Newcomer period between 7 and 90 days.
- Skill list (R2) is managed by the tenant; employees cannot add free-text skills unless the tenant allows it.

## 7. Tenant and organization scope
- Tenant-owned data: profiles, reporting lines, visibility policy, locations, skills.
- Organization scoping: "same subtree" visibility uses the viewer's memberships; HR visibility uses `people.hr_view` scope; organization administrators manage administrator-owned fields in their subtree.
- Cross-tenant denial cases: search, profile, org chart, My team and reporting line queries never return another tenant's people; a manager ID from another tenant in directory data is ignored.
- Sibling, parent and child boundary cases: a "same subtree" field of an Acme Gulf person is hidden from Acme Africa viewers; an HR officer of Acme Gulf sees HR fields of Acme Gulf people only; a parent-scope HR officer sees children only with subtree scope.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Profile fields | Directory or HR for synced fields; product for others | Internal (mobile phone, employee number Confidential) | Membership life plus 30 days | Yes |
| Reporting line history | Product (from directory) | Internal | 3 years | Yes |
| Photo | Directory or product object storage | Internal | Membership life | Yes |
| Skills and endorsements (R2) | Product | Internal | Membership life | Yes |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `people.directory` (directory, profiles, org chart, My team, newcomers) | on | on | on |
| `people.custom_fields` (tenant-defined fields) | off | on | on |
| `people.skills` (skills, endorsements, find an expert, R2) | off | on | on |

## 10. UX, localization and accessibility
- States: no results, person no longer available, field hidden, directory sync delayed (administrators only).
- Names show in the user's locale where a localized name exists, else the directory name.
- The org chart has a list view as an alternative to the tree; both are keyboard operable and announce position ("manager of 6 people").
- Phone numbers and emails are links; numbers keep left-to-right direction inside RTL text.

## 11. AI behavior
Agents read profiles only through this module's contract as the user, with the same visibility rules (spec 027). Profile data is sent to a model only when the user's question needs it, as Internal classification, except Confidential fields, which follow the AI policy for Confidential.

## 12. Integrations and events
- Consumes `UserProvisioned`, `UserDeprovisioned` and directory sync changes (spec 003). The directory reader adds `manager`, `jobTitle`, `department`, `businessPhones`, `mobilePhone`, `officeLocation`, `employeeHireDate`, `preferredLanguage` and the mapped location attribute. Consumes `LeaveApproved` and `LeaveCancelled` (022, R2) and HR record changes (019, R2).
- Publishes `ProfileUpdated`, `ReportingLineChanged`, `LocationAdded`.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| people.visibility_changed | audit | field, old and new rule, actor |
| people.field_source_changed | audit | field, old and new source, actor |
| people.manager_set_manually | audit | person, manager, actor |
| people.profile_updated | audit | person, field names (no values), actor |
| people.reporting_cycle_detected | operational | people involved, sync run |
| people.searched | analytics | pseudonymous user, result count bucket (no query text) |

## 14. Security and abuse cases
- Enumerating a restricted unit through search: results apply visibility, and counts never reveal hidden people.
- Scraping the directory: per-user rate limit and paging limits; exports only for administrators and audited.
- Inferring a hidden field from sort order or filters: sorting and filtering use only fields visible to the viewer.
- Impersonating a manager by editing one's own manager: the manager field is never employee-editable.
- Photo abuse: type and size checks; administrators can remove a photo, audited.

## 15. Non-functional requirements
- People search p95 under 300 ms at 50,000 members.
- Org chart of a manager with 200 reports renders under 1 second.
- Visibility changes effective within 60 seconds in the API.

## 16. Success criteria
- **SC-001**: An employee finds a colleague by name or title in under 15 seconds in usability tests.
- **SC-002**: Every approval that needs a line manager in specs 009, 016 and 025 resolves from this spec's reporting line in the pilot tenant.
- **SC-003**: A security review finds no API path that returns a field outside its visibility rule.

## 17. Open questions
- [NEEDS CLARIFICATION: should employees be able to hide their whole profile from search?] Default: no; tenants can narrow field visibility instead.
- [NEEDS CLARIFICATION: is the directory or the HR system the source of truth for job title when both are connected (R2)?] Default: a tenant setting per field, directory by default.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| EXP-005 | PPL-001, PPL-006 to PPL-010, US1, US2, US4 |
| EXP-006 | PPL-002, PPL-003, PPL-016, US3 |
| EXP-008 | PPL-007 |
| UC-05 | US1, US6 |
| Location dimension for 009, 021, 022, 025 | PPL-011, US5 |
| Reporting line for 009, 016, 025, 026 | PPL-004, PPL-005, US2 |
