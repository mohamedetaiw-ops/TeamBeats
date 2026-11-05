# ADR-0004: Isolate development-only authentication by composition, not by deletion

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-04 |
| Owners | Mohamed |
| Related specs | 003 |

## Context and constraints
The foundation requires temporary username/password sign-in for development, its removal from production before beta (IAM-003), and one code line for all deployments (C-20). Read literally, these conflict.

## Options considered
1. **Delete the dev provider before beta**: loses local tooling and test convenience.
2. **Runtime flag only**: one misconfiguration exposes password sign-in in production.
3. **Separate assembly referenced only by the Development/Test host**, plus a production startup guard and a CI test on the production artifact.

## Decision
Option 3. The production host project has no reference to the dev auth assembly. If a production configuration enables dev auth, startup fails. CI inspects the production artifact and fails if the dev auth assembly or its endpoints are present. No local credential tables exist in production migrations.

## Consequences and risks
Two host compositions to maintain (small). Test automation uses a test authentication handler, not the dev sign-in.

## Security and multi-tenancy implications
Removes the risk of a password login reaching production while keeping a single code line.

## Rollout and migration plan
Implement in Phase 1 alongside the dev sign-in itself.
