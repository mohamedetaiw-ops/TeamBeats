# CI runners

All GitHub Actions jobs run on GitHub-hosted runners (`runs-on: ubuntu-latest`). The repository is public, so hosted minutes are free. PostgreSQL 18 runs as a `services:` container in the jobs that need it.

## Rules for a public repository
- Never register a self-hosted runner on this repository. Anyone can open a pull request, a pull request can change the workflow files, and a self-hosted runner would then run that code on the machine that hosts it. A condition in a workflow file does not protect against this, because the pull request can remove it.
- Secrets are only used by `deploy-staging` (push to `main`, or started by hand) and `fly-access-check`. GitHub never passes secrets to pull requests from forks. Do not add `pull_request_target` or `workflow_run` triggers that check out pull request code, because those run with secrets.
- Pin third-party actions that receive secrets to a commit SHA, as `deploy-staging` does for `setup-flyctl`.

## Repository settings that keep this safe
- **Settings, Actions, Runners:** no runners listed.
- **Settings, Actions, General, Fork pull request workflows from outside collaborators:** require approval for all outside collaborators.
- **Settings, Actions, General, Workflow permissions:** read repository contents.
- **Settings, Branches:** protect `main` (pull request required, the `ci` checks required, no force pushes).

## History
From 2026-11-06 to 2026-11-07 CI ran on two self-hosted Windows runners while the repository was private and out of hosted minutes. That setup was removed before the repository went public. If a self-hosted runner is ever needed again, the repository must be private first, and the runner should live in a disposable VM, not on a personal computer.
