# Implementation Plan: Notifications and Email

**Spec:** `specs/015-notifications/spec.md` (v0.1, Proposed) | **Date:** 2026-11-07
**Shared base:** ADR-0006 (outbox), ADR-0013, module map (`Notifications`)

## Summary
A `Notifications` module with `INotificationSender.SendAsync(NotificationRequest)` in `Notifications.Contracts`. A send writes the in-app rows and an outbox message per email recipient in one transaction. A worker renders the template and sends through `IEmailSender`, with an SMTP adapter on .NET's `System.Net.Mail` or MailKit (MIT) if the base library proves insufficient for TLS and auth needs; a fake that stores messages for tests and dev. Templates are resource files per locale. The header bell uses `GET /api/v1/me/notifications/count`, cached per user for 30 seconds.

## Data model (schema `notifications`)
`notification` (user, category, mandatory, template key, parameters, link, read_at, dedupe_key), `preference` (user, category, email, digest), `delivery` (notification, channel, address, status, attempts), `tenant_settings` (sender name, relay reference, defaults).

## API (v1)
`GET /api/v1/me/notifications`, `POST /api/v1/me/notifications/{id}/read`, `POST /api/v1/me/notifications/read-all`, `GET/PUT /api/v1/me/notification-preferences`, `GET/PUT /api/v1/notifications/settings`, `POST /api/v1/notifications/settings/test`.

## Staging
Staging uses the fake email sender with a dev inbox page, so no external SMTP is needed until a provider is chosen.

## Tests
Delivery within 5 seconds; isolation per user and tenant; dedupe; email on and off; mandatory exempt; digest; retry and bounce; disabled members; 100,000 burst. Acceptance (e2e): bell, centre and preferences in English and Arabic with axe.
