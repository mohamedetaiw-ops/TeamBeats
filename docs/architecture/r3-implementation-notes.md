# R3 implementation notes

What was built for R3, slice by slice, and where it differs from the specs and plans. Each difference is either covered by an ADR or listed as a follow-up.

## Spec 026 R3 slice 1: four-eyes approval of critical settings (FR-016, T032 to T034)

- The spec names the critical settings and nothing else; the defaults are in the spec 026 R3 plan. The switch is a tenant setting of its own (`security.four_eyes`), off by default. Turning it on needs two active people who hold `security.manage`, so a tenant with one administrator cannot lock itself out; turning it off is held like any other change.
- Held kinds: `security.policy` (`security.manage`), `identity.provider` (`identity.config_manage`), `audit.retention` (`audit.retention_manage`) and `security.four_eyes`. Key changes join as `security.keys` with customer-managed keys (slice 2).
- `IFourEyes` and `IFourEyesApplier` are in `Security.Contracts`. The owning endpoint checks the request as usual (validation, ETag, permission, step-up where the endpoint has it) and then calls `HoldAsync`; with the switch on it stores the request in `security.pending_change` (migration 0009) and answers 202 with the pending change. Identity and Audit reach Security only through these contracts. Requests hold no secrets (the sign-in configuration carries a secret reference only).
- Approving needs the kind's permission over the tenant, a fresh sign-in, and not being the requester. The applier runs as the approver through the direct path, so the owning module validates again and writes its usual audit entry; Security audits `security.change_requested`, `_approved`, `_rejected`, `_withdrawn` and `_expired` with requester and decider. A change whose apply fails stays pending.
- One pending change per kind (`conflict.four_eyes.pending`, unique index). Changes expire after 72 hours: on the hourly access review sweep, and on listing and holding.
- The other holders of the permission get a security notice when a change waits; the requester gets one when it is decided or expires. Withdrawing tells no one.
- Pages: Security > Approvals (`/admin/security/changes`) with the switch and the list; the policy page shows a notice while a policy change waits and says "waits for approval" after a held save. The identity and retention pages do not have their own notice yet; the approvals page lists every kind.
