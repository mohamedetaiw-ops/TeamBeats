# Implementation Plan: Notifications and Email

**Spec:** `specs/015-notifications/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0006 (outbox), ADR-0013, module map (`Notifications`)

## Summary
A `Notifications` module with `INotificationSender.SendAsync(NotificationRequest)` in `Notifications.Contracts`. A send writes the in-app rows and a `delivery` row per email recipient in one transaction with the caller. The `delivery` table is the email queue, because the outbox has no delayed delivery and quiet hours, digests and retries need a not-before time; the worker polls it per tenant. It renders the template and sends through `IEmailSender`, with an SMTP adapter on .NET's `System.Net.Mail` (STARTTLS and authentication are enough for R1, so no mail library is added) and a capture sender that stores messages for tests, development and staging. Templates are resource keys per locale with tenant overrides. The header bell polls `GET /api/v1/me/notifications/count` every 30 seconds while the page is visible.

## Data model (schema `notifications`)
`notification` (user, category, mandatory, template key, parameters, link, read_at, dedupe_key), `preference` (user, category, channel flags, digest), `user_settings` (user, quiet hours start and end, time zone, digest time, last digest), `delivery` (notification, channel, address, status, attempts, not before), `address_state` (paused addresses after a hard bounce), `tenant_settings` (sender name, relay with secret reference, default email per category, extra mandatory categories, version), `template_override` (template key, locale, title, body), `captured_mail` (development and staging only). Grouping is kept on the `notification` row (group key and count).

## API (v1)
`GET /api/v1/me/notifications`, `GET /api/v1/me/notifications/count`, `POST /api/v1/me/notifications/{id}/read`, `POST /api/v1/me/notifications/read-all`, `GET/PUT /api/v1/me/notification-preferences`, `GET/PUT /api/v1/notifications/settings`, `POST /api/v1/notifications/settings/test`, `GET/PUT/DELETE /api/v1/notifications/templates/{key}/{locale}`. Development host only: `GET /api/v1/dev/notifications/mailbox`, `POST /api/v1/dev/notifications/sample`.

## Grouping and quiet hours
Grouping is applied when the in-app row is written: an existing row with the same group key inside the window is updated with a count. Held deliveries for quiet hours are `delivery` rows with a not-before time at the end of the user's quiet hours.

## R2 channels
`IChannelSender` per channel: email (R1), Teams (Microsoft 365 connection through `Integrations.Contracts`), web push (030 adapter), Slack and SMS (017 runtime), and a signage feed endpoint that reads published announcements through the 009 contract.

### R2 build slices
Push (spec 030) and Slack direct messages (spec 017) were built with those specs; signage screens exist per content site (spec 009 R2). The rest of R2 is built in these slices:

1. **Teams activity notifications (T011).** A Microsoft 365 connection feature `teams` with the application permission `TeamsActivity.Send` only, behind capability `notifications.teams` (on in every package). `ITeamsMessaging` in Integrations.Contracts finds the person by the Entra object ID their account is linked under (directory sync or Entra sign-in), never by email, and posts to `users/{id}/teamwork/sendActivityNotification`. The link opens in the TeamBeats Teams app when the platform registers one (`Integrations:Microsoft365:TeamsAppId`), otherwise in the browser. A person turns Teams on per category; mandatory categories go to Teams whenever it is available. When Teams cannot reach the person (app not installed, no linked account, connection or feature off), the delivery is dropped with `teams_not_installed`, `teams_not_linked` or `teams_off` and the message goes by email (US5 scenario 3); technical failures retry for up to 2 hours like Slack.
2. **Teams quick actions (T010, T011 rest).** Approve and reject from a Teams message, through a Teams bot that calls the spec 016 endpoints as the person, with step-up handed back to TeamBeats.
3. **SMS for mandatory messages (T012 rest).** A generic SMS gateway pack (`sms`, spec 017 runtime) posts `{ to, from, text, reference }` to `{baseUrl}/messages` with the API key as a bearer token, so any gateway with that shape, or a small relay in front of one, works; the spec names no provider. Behind capability `notifications.sms` (Enterprise and Government) with the monthly limit `notifications.sms_monthly_messages`, 2,000 by default as a commercial value the spec leaves open. A mandatory message queues one SMS (a unique delivery row) for a person with no email and no push device whose directory entry holds a mobile number; the number is read when the SMS goes out, normalized to E.164 and never stored by Notifications. The SMS carries the title and the link only, fitted to one 160 character segment, and the runtime keeps neither the number nor the text in run history (`KeepInput: false`). Technical failures retry for 30 minutes with the same reference and count once; a refused number, a turned off gateway or the limit drops the delivery with `sms_number_refused`, `sms_off` or `sms_quota`. Gap: an account needs an email today (provisioning skips people without one, spec 003 FR-003, and emails are unique per tenant), so the emailless frontline accounts this channel serves wait on a spec 003 decision about accounts without email.
4. **Signage by location (T013).** Screens registered to a location, showing announcements marked for signage, with withdrawal and revocation inside 1 minute.
5. **Delivery reports (T014).** Counts by channel for audience sends.

## Email outside the tenant's accounts
`IExternalEmail` (spec 022 FR-016, pre-boarding) sends one email now to an address that is not an account: same templates and tenant relay, the tenant's default language, no inbox entry and no queue. The calling module keeps its own state and retries when the outcome is `retry`; a `bounced` address is not paused here because no account owns it.

## Staging
Staging uses the fake email sender with a dev inbox page, so no external SMTP is needed until a provider is chosen.

## Tests
Delivery within 5 seconds; isolation per user and tenant; dedupe; email on and off; mandatory exempt; digest; retry and bounce; disabled members; 100,000 burst. Acceptance (e2e): bell, centre and preferences in English and Arabic with axe.
