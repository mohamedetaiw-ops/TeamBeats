# Row-level security exceptions

Every table with a `tenant_id` column has RLS enabled and forced (`platform.enable_tenant_rls`). The tables below are the only exceptions. `tests/Security/DatabaseIsolationTests.cs` fails if any other table lacks forced RLS, and also fails if a table listed here gains RLS without being removed from the list.

| Table | Why it is not under RLS | Compensating control |
|---|---|---|
| `messaging.outbox` | The dispatcher reads pending messages for all tenants in one pass (ADR-0006). | Rows are written only inside the producing transaction, which already runs under the producer's tenant context. The `tenant_id` column travels as a message header and consumers re-establish context from it. `tb_app` cannot read payloads through any API. |
| `messaging.inbox` | Platform messages (for example `PackagePublished`) have no tenant, so inbox rows can carry a null `tenant_id`, which a tenant policy cannot express. | Holds only consumer name, message ID and tenant ID, no business data. |
| `messaging.dead_letter` | Written when tenant context is missing or invalid, which is exactly when RLS context cannot be set. | Operator-only diagnostics; no tenant API reads it. |
| `identity.verified_domain` | One tenant per verified email domain per installation (spec 003 section 6) must be enforced across tenants. | `tb_app` has SELECT only; writes are platform operations. Holds only the domain and the owning tenant ID. |

Platform tables without a `tenant_id` column (`tenancy.tenant`, `entitlements.package*`, `audit.platform_audit_event`, `identity.platform_*`) are platform data by design and are reached only through platform-host endpoints guarded by `RequirePlatformOperator`.
