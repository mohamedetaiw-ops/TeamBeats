# SIEM event schema (spec 026 US4)

TeamBeats streams each tenant's audit stream (spec 006) to its SIEM destinations. That stream carries every audited
action, including all security events: sign-ins and refusals, step-up, security policy changes, network refusals,
alerts and their handling, role and permission changes, guest changes, SIEM configuration and delivery failures, and the
public API and MCP refusals and writes of spec 035. Events are sent in stream order, within 60 seconds once the
destination is reachable.

## Event

| Field | Type | Meaning |
|---|---|---|
| `id` | UUID | The audit event ID. The same event always has the same ID, so a receiver drops repeats by ID. |
| `tenant` | UUID | The tenant. A destination only ever receives its own tenant's events. |
| `time` | RFC 3339 time | When the action happened. |
| `position` | integer | The event's place in the tenant's stream: gapless and increasing. `null` for a test event. |
| `action` | string | The audit action, for example `auth.sign_in_refused` or `security.policy_changed`. |
| `outcome` | string | `success`, `failure` or `denied`. |
| `actor.type` | string | `user`, `app`, `system`, `platform_operator` (break-glass, spec 001) or `anonymous`. |
| `actor.id` | UUID or null | The user or app. |
| `target.type` | string | The kind of thing acted on, for example `security_policy`. |
| `target.id` | string or null | Its ID. |
| `target.orgUnit` | UUID or null | The org unit it belongs to, when it has one. |
| `sourceIp` | string or null | The client address, taken only from the connection or trusted proxies. |
| `correlationId` | string or null | The request ID, the same as in the TeamBeats logs. |
| `changes` | object or null | A summary of what changed, with before and after values where they apply. It never contains secrets, tokens or the content people read. |

## HTTPS JSON

`POST` to the destination URL with a JSON body `{"batch": "<batch id>", "events": [ ... ]}`, up to 500 events, in order.
The request is signed like TeamBeats webhooks (spec 017 FR-013):

- `X-TeamBeats-Timestamp`: Unix seconds.
- `X-TeamBeats-Signature`: `v1=` and the lowercase hex HMAC SHA-256 of `{timestamp}.{body}` with the signing secret.
- `X-TeamBeats-Delivery`: the batch ID. A batch that was received is not sent again under the same ID.
- `X-TeamBeats-Event`: `security.events`.

Answer with any 2xx status. 401 and 403 stop delivery until the destination is fixed; 408, 429 and 5xx are tried again.

## Syslog over TLS

RFC 5425 (TLS 1.2 or later, octet counting framing) carrying RFC 5424 messages, one per event:

```
<PRI>1 TIMESTAMP teambeats teambeats - ACTION - {event as JSON}
```

- `PRI` is facility 13 (log audit) with severity 4 (warning) for `denied` and `failure` outcomes and 6 (informational)
  otherwise, so `<108>` or `<110>`.
- `ACTION` is the audit action, cut to 32 printable characters.
- The default port is 6514. The server certificate must be valid for the host name.

## Delivery and replay

Each destination keeps its place in the stream. If a batch fails, the place stays and the batch is tried again with
backoff (10 seconds, doubling up to 5 minutes). Nothing is lost while the destination is down: the audit stream keeps at
least 365 days of events, so the destination catches up in order once it works again. A failure is recorded in the
audit stream at most once an hour, and after 24 hours of failures the tenant's security officers get an alert.

A new destination starts at the current end of the stream, or up to 7 days back when the administrator asks for it.
