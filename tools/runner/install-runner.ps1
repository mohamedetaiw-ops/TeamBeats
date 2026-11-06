#Requires -RunAsAdministrator
# Registers this Windows machine as the TeamBeats self-hosted GitHub Actions runner and installs it as a service.
# Run once in an administrator PowerShell, after saving a registration token. See docs/operations/ci-runner.md.
param(
  [string]$Repo = 'mohamedetaiw-ops/TeamBeats',
  [string]$Root = 'C:\actions-runner',
  [string]$Version = '2.338.2',
  [string]$Sha256 = 'a1f17aa987fc8158bc7521d284912267a0d01b7c2c4684b33c86783b5e8221cc',
  [string]$Labels = 'teambeats'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $Root 'config.cmd'))) {
  $ProgressPreference = 'SilentlyContinue'
  [Net.ServicePointManager]::SecurityProtocol = 'Tls12'
  New-Item -ItemType Directory -Force $Root | Out-Null
  $zip = Join-Path $env:TEMP "actions-runner-$Version.zip"
  Invoke-WebRequest "https://github.com/actions/runner/releases/download/v$Version/actions-runner-win-x64-$Version.zip" -OutFile $zip -UseBasicParsing
  if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower() -ne $Sha256) { throw 'Runner download failed its checksum' }
  Expand-Archive $zip -DestinationPath $Root -Force
  Remove-Item $zip
}

# The job checkouts are deep enough for node_modules to pass the old 260 character limit.
& git config --system core.longpaths true

# An elevated shell cannot read the gh sign-in from the Windows credential store, so fetch the token first from a
# normal shell (see docs/operations/ci-runner.md). It is valid for one hour and this script deletes the file.
$tokenFile = Join-Path $env:LOCALAPPDATA 'teambeats-runner-token.txt'
if (-not (Test-Path $tokenFile)) { throw "Token file not found: $tokenFile" }
$token = (Get-Content $tokenFile -Raw).Trim()
Remove-Item $tokenFile
if (-not $token) { throw 'The token file was empty.' }

# With no --windowslogonaccount the service runs as NETWORK SERVICE, which cannot read your profile, your git
# credentials or your flyctl sign-in. Keep it that way.
Push-Location $Root
try {
  & .\config.cmd --unattended --replace --url "https://github.com/$Repo" --token $token --name $env:COMPUTERNAME --labels $Labels --work _work --runasservice
  if ($LASTEXITCODE -ne 0) { throw "Runner configuration failed with exit code $LASTEXITCODE" }
} finally {
  Pop-Location
}

Get-Service 'actions.runner.*' | Format-Table Name, Status, StartType
Write-Host 'Done. You can close this window.'
