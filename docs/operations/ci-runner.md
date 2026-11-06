# CI runner

All GitHub Actions jobs run on one self-hosted Windows runner, because the repository is private and ran out of hosted minutes. Self-hosted jobs use no minutes. The runner is named after its machine and lives in `C:\actions-runner`. Workflows select it with `runs-on: [self-hosted, windows, x64, teambeats]`.

## What this means day to day
- CI and staging deploys run only while the machine is on and awake. If it is off or asleep, jobs wait in the queue and start when it comes back. GitHub cancels a job that waits for 24 hours.
- The runner does one job at a time, so a push to `main` runs the CI jobs and the staging deploy one after another. Do not add a second runner on the same machine, because the backend and e2e jobs both need port 5432.
- Port 5432 must be free during a job. Stop a local PostgreSQL or the compose stack if CI fails to start PostgreSQL.
- Docker Desktop is not needed. PostgreSQL 18 comes from the official Windows binaries (`.github/actions/windows-postgres`), and Fly builds images remotely.

## How it differs from a hosted runner
- Windows runners cannot use `services:` or `container:`. Start what a job needs in a step, and stop it in an `if: always()` step.
- Scripts run in Git Bash (`defaults.run.shell: bash`). Use `shell: powershell` only where a step needs Windows tools.
- Tools are installed once into the runner tool cache (`_work/_tool`) with pinned versions and checksums. To change PostgreSQL or jq, update the version and checksum in the action or workflow.
- `.gitattributes` keeps files LF on checkout. Without it, shell scripts break and the Fly images get CRLF files.

## Security
- Every job has a condition that skips pull requests from forks. Keep it on any new job, because a fork's code would otherwise run on this machine.
- The service runs as `NETWORK SERVICE`, so jobs cannot read the owner's profile, git credentials or flyctl sign-in. Do not change it to a personal account.
- Keep the repository private. GitHub advises against self-hosted runners on public repositories.

## Install or reinstall
1. Install Git for Windows and sign in with `gh auth login`.
2. In a normal PowerShell, save a registration token: `gh api -X POST repos/mohamedetaiw-ops/TeamBeats/actions/runners/registration-token --jq .token > "$env:LOCALAPPDATA\teambeats-runner-token.txt"`. It is valid for one hour.
3. In an administrator PowerShell, run `tools/runner/install-runner.ps1`. It reads the token and deletes the file.

To remove it, run `.\config.cmd remove --token <token>` in `C:\actions-runner` from an administrator PowerShell. Get the token from **Settings, Actions, Runners** in the repository.

## If jobs stay queued
Check that the machine is awake, then check the service (`Get-Service actions.runner.*`) and its logs in `C:\actions-runner\_diag`. To go back to hosted runners, restore `runs-on: ubuntu-latest` and the `services:` blocks from the git history.
