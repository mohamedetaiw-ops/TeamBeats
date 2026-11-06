# ADR-0006: Messaging library for outbox and RabbitMQ

| Field | Value |
|---|---|
| Status | Accepted (2026-11-05 by Mohamed) |
| Date | 2026-11-04 |
| Owners | Mohamed |
| Related specs | 001 to 006 (domain events), r0-solution-structure section 4 |

## Context and constraints
R0 needs a transactional outbox, RabbitMQ publishing, idempotent consumers, retries and dead-lettering. Constitution XXII forbids commercial or non-OSI dependencies without an ADR, because the product ships to customer infrastructure. MassTransit moved to a commercial licence from v9.

## Decision drivers
Licence safety, low operational footprint, correctness of the outbox with EF Core and PostgreSQL RLS (tenant context per message), testability.

## Options considered
1. **MassTransit v9**: mature, but commercial licence for production use. Rejected without a separate commercial decision.
2. **MassTransit v8 (Apache 2.0)**: free, but end-of-life path; locking to an old major version is a long-term risk.
3. **Wolverine (MIT)**: outbox, inbox, retries and RabbitMQ transport built in, good PostgreSQL support. Brings its own handler model and runtime conventions, which are opinionated.
4. **In-house outbox on `RabbitMQ.Client` (MIT/Apache 2.0)**: small, fully controlled, explicit tenant context handling. We write and maintain retry, dead-letter and inbox code (a few hundred lines).

## Decision (proposed)
Option 4. The outbox and inbox are small, central to tenant isolation (every message must carry and re-establish tenant context), and easier to verify when the code is ours. Revisit Wolverine if the in-house code grows beyond simple publish, consume, retry and dead-letter.

## Consequences and risks
- More code to own and test; mitigated by a dedicated test suite (duplicate delivery, crash between commit and publish, poison message).
- No saga or scheduling features; R0 does not need them. Scheduled jobs use a simple PostgreSQL-backed scheduler in the worker host.

## Security and multi-tenancy implications
Each message has `tenant_id`, `correlation_id`, `message_id` headers. Consumers open a transaction and set tenant context per message (ADR-0002). Messages without a tenant header are rejected unless the message type is registered as platform-level.

## Rollout and migration plan
Implement in R0 task 001/T009 before the first domain event.
