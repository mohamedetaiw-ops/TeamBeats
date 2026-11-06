#!/usr/bin/env bash
# deploy-lint (constitution V "deployment manifest lint", spec 003 US7, ADR-0004).
# Fails when a production deployment file could bring in development sign-in, test accounts, the test tenant or fake
# external systems.
# Production files: the production Dockerfiles, and any file under deploy/ or .github/workflows/ whose path
# contains "prod". Staging files must say "staging" in their path and are not checked here.
# Usage: tools/deploy-lint.sh [repo-root]
set -euo pipefail
root="${1:-.}"
cd "$root"

files=$( { printf '%s\n' deploy/docker/api.Dockerfile deploy/docker/workers.Dockerfile deploy/docker/web.Dockerfile;
           find deploy .github/workflows -type f 2>/dev/null | grep -i 'prod' || true; } | sort -u)

forbidden='Api\.DevHost|Identity\.DevAuth|DevFakes|Dev__Fakes|api-staging\.Dockerfile|DevSignIn|TestAccounts|SeedTenant|TEAMBEATS_DEV_SIGNIN|ASPNETCORE_ENVIRONMENT[=: "]+(Development|Test|Staging)'

status=0
for f in $files; do
  [ -f "$f" ] || continue
  # Lines that assert absence ("test ! -f ...DevAuth.dll") are the guard itself and are allowed.
  if hits=$(grep -nE "$forbidden" "$f" | grep -vE 'test ! -f'); then
    echo "deploy-lint: $f is a production deployment file but references development sign-in or test setup:" >&2
    echo "$hits" | sed 's/^/  /' >&2
    status=1
  fi
done

if [ "$status" -eq 0 ]; then
  echo "deploy-lint: $(echo "$files" | grep -c .) production deployment file(s) clean."
fi
exit "$status"
