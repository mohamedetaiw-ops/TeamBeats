# Redundant entry review (WCAG 3.3.7)

Spec 038 A11Y-021, task T021. WCAG 2.2 success criterion 3.3.7 asks that information a person already gave in a process is filled in for them or offered to pick, unless entering it again is essential, needed for security, or the earlier answer is no longer valid.

Reviewed on 2026-11-18 against the code in `src/Web` and the flows of the specs named below. Each row is a process with more than one step, or one a person comes back to. Forms of a single step (leave, letters, attestation) have nothing to carry forward and are left out.

| Process | Steps | What is carried forward | Result |
|---|---|---|---|
| Sign in (spec 003) | Email, then the identity provider or a code | The email field offers browser autofill (`autocomplete="email"`) and takes paste; a verification code field takes a pasted code and one-time-code autofill (`a11y/authentication.spec.ts`). | Meets |
| Service request (spec 016) | Form, then submit | Fields with a prefill source take the value from the profile or HR record (spec 019), shown read only when HR owns it. A request prepared by the assistant (spec 014) opens with its answers in place. Choosing "on behalf of" refreshes only the prefilled fields and keeps what was typed. | Meets |
| Request returned for changes (spec 016) | Returned, then corrected and sent again | The edit page opens with every earlier answer (`/requests/[id]/edit` passes the request's data as the initial values). | Meets |
| Delegation of approvals (spec 016) | Form, confirm, receipt | The confirm step shows the chosen delegate and dates; Back returns to the form with them kept. Opening it again starts from the current delegation. | Meets |
| Expense claim (spec 022) | Lines with receipts, then submit | Reading a receipt fills the amount, date and merchant for the person to check. Lines stay while more are added. | Meets |
| Add connection (spec 006) | Choose a type, set up, test, turn on | Setup is saved before the test step; test and turn on read the saved connection, and settings are changed later on the connection's own page. | Meets |
| Booking a space (spec 031) | Find, then book | The date and times chosen in the search are sent with the booking, for one desk or a whole team; nothing is asked twice. | Meets |
| Employee data change (spec 022) | Choose a group, enter the new values, submit | Values typed stay when the person moves to another group and back. | Meets |

## Exceptions that are allowed

- Step up sign-in before saving a credential or an admin change (spec 003, spec 026) asks for the code again on purpose: re-entry for security is an exception in the criterion.
- A session that ended asks for the email again at sign in: that is a new process, not a step of the one that ended.

## How it stays true

- Prefill sources are part of a service's form definition (spec 016, spec 019), so a new service form offers them without new code.
- New multi-step flows are added to this table in the same pull request, and the map row for 3.3.7 points here.
