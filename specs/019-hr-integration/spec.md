# Feature Specification: HR System Integration and Payslips

| Field | Value |
|---|---|
| Spec ID | 019-hr-integration |
| Area | integrations |
| Status | Approved |
| Version | 1.0 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R2 (canonical employee record from an HR system, HR lifecycle events for joiners, movers and leavers, HR record prefill in service forms, payslip view, leave balance source of truth option, generic HR REST and SFTP packs), R3 (named packs for Oracle HCM and SAP SuccessFactors, employment letters generated from HR data, compensation statement) |
| Last updated | 2026-11-07 |
| Approved | 2026-11-07 by Mohamed (standing approval to proceed) |
| Requirement IDs | INT-007, UC-09, HRI-001 to HRI-018 (new) |
| Use cases | UC-09 |
| Depends on | 002 (organization hierarchy), 003 (identity lifecycle), 004 (authorization), 005 (entitlements), 006 (Connections area), 010 (profiles and HR fields), 017 (connector runtime), 026 (step-up, Confidential data) |
| Used by | 010 (HR fields, reporting line), 016 (form prefill), 022 (joiner, mover and leaver journeys, leave balances), 021 (headcount counts) |

## 1. Problem and outcome

**Problem:** The HR system is the source of truth for who works where, under which manager, with which grade and contract. Today employees re-type their employee number, department and manager into every form, HR keeps a second copy of the org in the intranet by hand, new hires get access days late, and leavers keep it days too long. Payslips sit in a separate payroll portal with its own password.

**Outcome:** An HR connection (spec 017) feeds a canonical employee record into TeamBeats. Joiner, mover and leaver events from HR start the journeys of spec 022 and update the hierarchy (002), identity lifecycle (003) and profile (010). Forms prefill the requester's HR data. Employees read their own payslips in TeamBeats, protected as Confidential data with step-up. The directory part of identity (users and groups) keeps coming from Entra ID or Active Directory (specs 002, 003); HR adds the employment facts those systems do not hold.

## 2. Scope

**In scope:**
- Canonical HR contract: employee (employee number, legal name in ar and en, work email, employment status, hire date, termination date, job title, grade, employment type, cost centre, location, org unit code, manager employee number), position, org unit code mapping, payslip summary and document.
- Matching HR employees to TeamBeats users by work email or employee number, with an unmatched list.
- Scheduled sync and inbound change events (webhook through spec 017), with field ownership: HR owns the fields it maps, they are read-only in TeamBeats.
- Lifecycle events: `EmployeeHired`, `EmployeeTransferred`, `EmployeeManagerChanged`, `EmployeeTerminated`, `EmployeeRehired`, with effective dates and future-dated changes held until effective.
- Org unit mapping from HR codes to the hierarchy of spec 002, without HR creating or deleting units on its own unless the administrator turns that on.
- Prefill of service forms (spec 016) with the requester's HR fields the form owner chose.
- Payslip list and view: period, gross, deductions, net, currency and the PDF, read from payroll or HR, for the employee only.
- Leave balances: optional source of truth in HR, read by spec 022 instead of its own ledger when the tenant chooses.
- Generic packs in R2: HR REST pack (configurable through the custom REST builder with an HR mapping template) and SFTP and CSV import.

**Out of scope:**
- Writing back to the HR system, except the hand-offs specified by 022 (for example an approved leave request) through 017.
- Payroll calculation, tax, compensation planning, performance reviews.
- Oracle HCM and SAP SuccessFactors named packs (R3).
- Recruitment and applicant tracking.

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Integration administrator | Create the HR connection, field mapping, matching rules, org code mapping, schedules | `integration.manage` |
| HR administrator | See sync results, resolve unmatched employees, approve org unit changes proposed by HR, choose the leave balance source | `hr.integration_manage` at tenant scope |
| HR officer | See HR fields of employees in scope | `people.hr_fields.read` in org scope (spec 010) |
| Employee | See own HR fields and own payslips | Self, step-up for payslips |
| Line manager | See HR fields marked visible to managers for direct reports | `people.hr_fields.read` relation `manager_of` |
| Service owner | Choose which HR fields prefill a form | `service.manage` |

## 4. User scenarios

### User Story 1: Connect the HR system and match employees (Priority: P1, R2)

Ahmed connects Acme's HR system with the HR REST pack and maps its fields to the canonical contract. The first sync matches 4,820 of 4,900 employees.

**Why this priority:** every other story depends on matched employees.

**Independent test:** connect a fake HR system with 100 employees, 3 without a matching user and 1 with a duplicate email, run the first sync, and check the matched, unmatched and conflict lists.

**Acceptance scenarios:**
1. **Given** an HR connection and mapping, **When** the first sync runs, **Then** each HR employee is matched by the tenant's rule (work email first, then employee number), and results show matched, unmatched and conflicting records.
2. **Given** two HR employees with the same work email, **Then** neither is matched automatically and both appear as a conflict for the HR administrator.
3. **Given** an unmatched HR employee, **When** the HR administrator links them to a user, **Then** the link is kept for later syncs and audited.
4. **Given** a matched employee, **Then** mapped HR fields on the profile (spec 010) show "from HR" and cannot be edited in TeamBeats.
5. **Given** a sync with more than the tenant's change threshold (default 10% of employees terminated or moved in one run), **Then** the run stops before applying changes and asks the HR administrator to confirm. *(protects against a bad export)*

### User Story 2: Joiners get access on day one (Priority: P1, R2)

Sara is hired with a start date of 1 December.

**Acceptance scenarios:**
1. **Given** an `EmployeeHired` event with a future start date, **Then** it is held until the configured lead time (default 5 working days before start), then spec 022 starts the onboarding journey and the profile is prepared.
2. **Given** the identity for Sara does not yet exist in Entra ID or AD, **Then** the onboarding journey waits for it and matches automatically when the directory sync creates it.
3. **Given** a hire cancelled in HR before the start date, **Then** the journey is cancelled and its open tasks are withdrawn with a reason.

### User Story 3: Movers and manager changes keep the org right (Priority: P1, R2)

Omar moves from Finance to Procurement with a new manager on 1 January.

**Acceptance scenarios:**
1. **Given** an `EmployeeTransferred` event effective on a date, **When** that date arrives, **Then** Omar's org unit (spec 002 membership), manager (spec 010 reporting line), title and location change together, and spec 022 starts the mover journey.
2. **Given** the move, **Then** authorization changes follow spec 004 rules for the new unit; open approvals assigned to Omar as a manager in Finance are reassigned by spec 016 delegation rules, not dropped.
3. **Given** an HR org code with no mapping to a TeamBeats unit, **Then** the move is held, the HR administrator is asked to map the code, and nothing else in the run is blocked.
4. **Given** "HR may create org units" is off (default), **Then** a new HR org code is proposed as a unit for approval and is never created silently.

### User Story 4: Leavers lose access on time (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** an `EmployeeTerminated` event with a last working day, **Then** spec 022 starts the offboarding journey at once, and on the end of the last working day (tenant time zone) the user is disabled in TeamBeats (spec 003) whether or not the directory has disabled them yet.
2. **Given** an immediate termination flagged by HR, **Then** the user is disabled within 15 minutes and active sessions are revoked.
3. **Given** a rehire of a former employee, **Then** the old user is reactivated and relinked when the tenant allows, otherwise a new user is linked, and history stays with the original record.

### User Story 5: Forms already know who I am (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** a service form whose owner chose employee number, grade, cost centre and manager as prefill, **When** Laila opens it, **Then** those fields are filled from her HR record and marked as prefilled.
2. **Given** a prefilled field the owner marked read-only, **Then** Laila cannot change it, and the server uses the HR value, not what the browser sends.
3. **Given** HR data that is older than 7 days for a field the form marks "must be current", **Then** a live read is made through the connector, and if it fails the form explains the field could not be confirmed.
4. **Given** an HR field classified Confidential (for example salary grade), **Then** it can be prefilled only into a form whose classification is Confidential or higher.

### User Story 6: Read my payslip (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** payslips in payroll, **When** Laila opens My payslips, **Then** after step-up (spec 026) she sees a list by period with net pay, and can open the summary and the PDF.
2. **Given** anyone other than Laila, including her manager, HR officers and tenant administrators, **Then** they cannot read her payslips in TeamBeats; there is no administrator override. *(the HR system remains the place for HR staff)*
3. **Given** a payslip PDF, **When** it is downloaded, **Then** the download is audited, carries a watermark with the user and time, and is never cached on shared devices (spec 030 kiosk mode hides payslips entirely).
4. **Given** the payroll system is unavailable, **Then** My payslips says so and shows nothing stale.
5. **Given** payslips, **Then** they are never indexed by search (012), never sent to models (013), and never placed in notifications beyond "your payslip for November is available".

### User Story 7: Use HR leave balances (Priority: P2, R2)

**Acceptance scenarios:**
1. **Given** the tenant chose HR as the leave balance source, **Then** spec 022 shows balances read from HR, hands approved requests to HR through 017, and shows "pending in HR" until HR confirms.
2. **Given** the HR read fails, **Then** the request form shows the last known balance with its time and allows submission only if the tenant allows it.

### Edge cases and exception flows
- HR record arrives before the directory user: held as unmatched and retried at each sync for 30 days.
- Manager loop in HR data (A manages B, B manages A): the change is refused for both and reported.
- Future-dated change later cancelled in HR: the held change is dropped.
- Two changes for one employee on the same effective date: applied in HR sequence order.
- Employee in two org units in HR (secondary assignment): primary from HR becomes the main membership, secondaries become additional memberships if the tenant maps them.
- Name in only one language: the other language shows the same value until provided.

## 5. Functional requirements

- **HRI-001 / FR-001**: The HR integration MUST use the connector runtime of spec 017 and the canonical HR contract; no module other than `Integrations` sees HR system DTOs. *(INT-007)*
- **HRI-002 / FR-002**: Matching MUST follow the tenant's ordered rule (default work email, then employee number) and MUST NOT auto-match conflicts.
- **HRI-003 / FR-003**: Fields mapped from HR MUST be read-only in TeamBeats and labelled with their source.
- **HRI-004 / FR-004**: A sync that exceeds the tenant's change threshold MUST stop before applying and require HR administrator confirmation.
- **HRI-005 / FR-005**: Lifecycle events MUST carry effective dates; future-dated changes MUST be held and applied on the effective date in the tenant time zone.
- **HRI-006 / FR-006**: `EmployeeHired`, `EmployeeTransferred`, `EmployeeManagerChanged`, `EmployeeTerminated` and `EmployeeRehired` MUST be published as product events consumed by 002, 003, 010 and 022.
- **HRI-007 / FR-007**: HR org codes MUST map to existing units; unmapped codes MUST hold only the affected change; creating units from HR MUST be off by default and approval based when on.
- **HRI-008 / FR-008**: Termination MUST disable the TeamBeats user at the end of the last working day, and within 15 minutes for immediate terminations, including session revocation.
- **HRI-009 / FR-009**: Form prefill MUST use server-side HR values for read-only fields and MUST respect field classification against form classification.
- **HRI-010 / FR-010**: Payslips MUST be readable only by the employee they belong to, after step-up, with no override role.
- **HRI-011 / FR-011**: Payslip downloads MUST be audited and watermarked; payslips MUST NOT be indexed, cached on shared devices, sent to models or included in notification bodies.
- **HRI-012 / FR-012**: Payslip and HR reads that fail MUST show the failure, not stale payslip data.
- **HRI-013 / FR-013**: Leave balance source MUST be a tenant setting (TeamBeats ledger or HR), versioned configuration per constitution XXIX.
- **HRI-014 / FR-014**: Manager loops and other invalid structures in HR data MUST be refused per record and reported.
- **HRI-015 / FR-015**: Unmatched HR records MUST be retried each sync for 30 days, then listed as stale.
- **HRI-016 / FR-016**: The HR REST pack MUST be configurable without code from a mapping template; the SFTP and CSV pack MUST support the same canonical contract.
- **HRI-017 / FR-017**: HR fields shown on profiles MUST follow the visibility rules of spec 010 (self, manager, HR officer in scope).
- **HRI-018 / FR-018**: Every applied change MUST be traceable to its HR run and source record ID.

## 6. Business and validation rules
- Default schedule: full sync nightly, change events as they arrive; payslip list read live with a 10 minute cache per user (never shared).
- Onboarding lead time default 5 working days, range 0 to 30.
- Change threshold default 10% of active employees for terminations or moves in one run.
- Payslip retention in TeamBeats: none; summaries are read live and not stored, PDFs are streamed.

## 7. Tenant and organization scope
- Tenant-owned data: HR connection, mappings, match links, run results, held changes.
- Organization scoping: HR officers see HR fields only in their org scope; org code mapping is tenant-wide.
- Cross-tenant denial cases: an HR record cannot match a user of another tenant even with the same email; payslip requests carry the tenant from the session only.
- Sibling, parent and child cases: an HR officer of Acme Gulf cannot see HR fields of Acme Africa employees.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Employee HR fields (copy) | HR system | Confidential (grade, cost centre), Internal (title, location) | While employed plus 30 days, then removed | Yes |
| Match link | Product | Internal | Life of the user | Yes (IDs) |
| Held future-dated changes | Product | Confidential | Until applied or cancelled | Yes |
| Payslip summary and PDF | Payroll or HR | Confidential | Not stored | Yes |
| Run results | Product | Internal | 90 days | Yes (IDs) |

## 9. Package entitlements

| Capability key | Workplace | Workplace Plus | Enterprise & Government |
|---|---|---|---|
| `hr.integration` | off | on | on |
| `hr.payslips` | off | on | on |
| `hr.form_prefill` | off | on | on |
| `hr.named_packs` (R3) | off | off | on |

## 10. UX, localization and accessibility
- My payslips is in the profile menu and as a service in the catalogue; numbers use the locale's digits and currency format.
- The HR integration page shows last run, counts, unmatched, conflicts, held changes and pending org codes.
- Arabic and English, RTL, keyboard operable; PDF payslips are shown as provided by payroll.

## 11. AI behavior
None. HR fields may be used by the assistant (014) only through the profile fields the user can already see; payslips are excluded from every AI path.

## 12. Integrations and events
- Publishes the lifecycle events of FR-006 and `HrSyncCompleted`, `HrSyncHeld`.
- Consumes connector triggers and sync jobs from spec 017.

## 13. Audit, telemetry and analytics

| Event | Type | Key fields |
|---|---|---|
| hr.mapping_changed | audit | connection, fields, actor |
| hr.match_linked / unlinked | audit | user, HR record, actor |
| hr.sync_held / confirmed | audit | run, counts, actor |
| hr.change_applied | audit | user, change type, effective date, run |
| hr.payslip_viewed / downloaded | audit | user, period |
| hr.sync | telemetry | counts, duration, status |

## 14. Security and abuse cases
- Bad export wiping the org: change threshold (FR-004).
- Payslip snooping by administrators: no override role (FR-010).
- Prefill tampering: server-side values for read-only fields (FR-009).
- Leaver keeping access: TeamBeats disables on its own timing (FR-008).

## 15. Non-functional requirements
- Full sync of 20,000 employees under 30 minutes.
- Payslip list under 2 seconds p95 when payroll answers within 1 second.

## 16. Success criteria
- **SC-001**: 99% of joiners have their profile and onboarding journey ready on their first day in pilot tenants.
- **SC-002**: Leavers are disabled in TeamBeats no later than the end of their last working day in 100% of test cases.

## 17. Open questions
- [NEEDS CLARIFICATION: which HR system does the first customer use?] Default: build the generic HR REST and SFTP packs first; a named pack follows the first signed customer.
- [NEEDS CLARIFICATION: may managers see any HR field of direct reports?] Default: only fields the tenant marks "visible to manager", none by default.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| INT-007 | HRI-001 to HRI-008, US1 to US4 |
| UC-09 | US5, US6, HRI-009 to HRI-012 |
