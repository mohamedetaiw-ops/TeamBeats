# CI runner

All GitHub Actions jobs run on two self-hosted Windows runners on one machine, because the repository is private and ran out of hosted minutes. Self-hosted jobs use no minutes. `MSAFAA` lives in `C:\actions-runner` and `MSAFAA-2` in `C:\actions-runner-2`, each as its own Windows service. Workflows select it with `runs-on: [self-hosted, windows, x64, teambeats]`.

## What this means day to day
- CI and staging deploys run only while the machine is on and awake. If it is off or asleep, jobs wait in the queue and start when it comes back. GitHub cancels a job that waits for 24 hours.
- Each runner does one job at a time, so two jobs run in parallel and the rest wait.
- Jobs on the two runners must not share ports. `.github/actions/windows-postgres` gives each runner its own: `MSAFAA` uses 5432 (PostgreSQL), 5080 (API) and 3000 (web), and `MSAFAA-2` uses 5433, 5081 and 3001. A third runner must be named `MSAFAA-3`, and the number in the name sets its ports.
- Those ports must be free during a job. Stop a local PostgreSQL, API or web app, or the compose stack, if CI fails to start them.
- Timing tests run while another job loads the machine. If one fails by a small margin, rerun the job before suspecting the code.
- Docker Desktop is not needed. PostgreSQL 18 comes from the official Windows binaries (`.github/actions/windows-postgres`), and Fly builds images remotely.

## How it differs from a hosted runner
- Windows runners cannot use `services:` or `container:`. Start what a job needs in a step, and stop it in an `if: always()` step.
- Scripts run in Git Bash (`defaults.run.shell: bash`). Every job needs `./.github/actions/windows-shell` right after checkout, or `bash` means the WSL launcher in `System32` and some actions cannot unpack their downloads. Use `shell: powershell` only where a step needs Windows tools.
- `PSExecutionPolicyPreference: Bypass` is set in each workflow, because Windows blocks PowerShell scripts for the service account.
- `gitleaks-action` cannot install on Windows, so the governance job installs gitleaks itself.
- Tools are installed once into the runner tool cache (`_work/_tool`) with pinned versions and checksums. To change PostgreSQL, jq or gitleaks, update the version and checksum in the action or workflow.
- `.gitattributes` keeps files LF on checkout. Without it, shell scripts break and the Fly images get CRLF files.

## Security
- Every job has a condition that skips pull requests from forks. Keep it on any new job, because a fork's code would otherwise run on this machine.
- The service runs as `NETWORK SERVICE`, so jobs cannot read the owner's profile, git credentials or flyctl sign-in. Do not change it to a personal account.
- Keep the repository private. GitHub advises against self-hosted runners on public repositories.

## Install or reinstall
1. Install Git for Windows and sign in with `gh auth login`.
2. In a normal PowerShell, save a registration token in the runner folder: `gh api -X POST repos/mohamedetaiw-ops/TeamBeats/actions/runners/registration-token --jq .token > C:\actions-runner\.registration-token`. It is valid for one hour.
3. In an administrator PowerShell, run `tools/runner/install-runner.ps1`. It reads the token and deletes the file. For the second runner, save the token in `C:\actions-runner-2` and add `-Root C:\actions-runner-2 -Name MSAFAA-2`.

To remove it, run `.\config.cmd remove --token <token>` in `C:\actions-runner` from an administrator PowerShell. Get the token from **Settings, Actions, Runners** in the repository.

## If jobs stay queued
Check that the machine is awake, then check the service (`Get-Service actions.runner.*`) and its logs in `C:\actions-runner\_diag`. To go back to hosted runners, restore `runs-on: ubuntu-latest` and the `services:` blocks from the git history.
