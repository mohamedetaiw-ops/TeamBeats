# Tasks: Notifications and Email

Depends on 004, 005, 006, 007, 008. R1: US1 to US4. R2: US5 to US8.

- [x] T001 `Notifications` and `Notifications.Contracts` projects; migration (forced RLS); module map confirmed
- [x] T002 Permission `notifications.manage`; audit actions; capability keys; platform mandatory categories
- [x] T003 [P] [US1] Tests: in-app within 5 seconds; open marks read; isolation; dedupe; disabled members
- [x] T004 [US1] Send contract, in-app centre endpoints, header count
- [x] T005 [P] [US2] Tests: email per locale with sender name; off and mandatory; digest; retry 24 hours; bounce pauses
- [x] T006 [US2] Email worker, `IEmailSender` with SMTP adapter and fake, templates, digest job
- [x] T007 [US4] Tenant settings, template overrides and relay test
- [x] T007a [P] [US1] [US3] Tests and implementation: grouping window; quiet hours hold and urgent bypass
- [x] T008 Bell, centre panel and page, preferences page, dev inbox page, in English and Arabic, RTL; e2e with axe
- [x] T009 Wire existing "notify" points (critical announcements, approvals, security alerts) once their modules exist; burst performance test; OpenAPI; spec evidence

## R2
- [ ] T010 [P] [US5] Tests: Teams delivery, quick actions with the same checks and step-up, fallback when disabled
- [ ] T011 [US5] Teams channel sender and quick actions
- [ ] T012 [US6] Push, Slack and SMS channel senders with one SMS per message
- [ ] T013 [US7] Signage screens and signed feed with revocation within 1 minute
- [ ] T014 [US8] Delivery reports for audience sends
