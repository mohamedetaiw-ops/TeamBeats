# ADR-0004: Isolate development-only authentication by composition, not by deletion

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-11-04, staging test account 2026-11-06 |
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

**Staging.** The shared staging deployment is a Test environment. It runs the development host (`deploy/docker/api-staging.Dockerfile`, environment `Staging`), never the production image. In `Staging`, development sign-in must run in test account mode: only test accounts listed in configuration can sign in, by username with one click ("Sign in as test admin" on the sign-in page) and no password or secret. The development host creates the fictional test tenant on start through the tenancy use case and appoints the test account as its administrator. Email sign-in and platform dev sign-in are off in test account mode. The production host also refuses to start if test accounts or a test tenant are configured, and `tools/deploy-lint.sh` fails CI if a production deployment file references the development host, the staging image, development sign-in settings or a non-production environment name (constitution V, deployment manifest lint).

## Consequences and risks
Two host compositions to maintain (small). Integration tests use a test authentication handler, not the dev sign-in. End-to-end tests against staging use the staging test account. Anyone who can open the staging URL can sign in as the test administrator, so staging must only ever hold fictional data and must not connect to a real identity provider, directory or Microsoft 365 tenant.

## Security and multi-tenancy implications
Removes the risk of a password login reaching production while keeping a single code line.

## Rollout and migration plan
Implement in Phase 1 alongside the dev sign-in itself.
