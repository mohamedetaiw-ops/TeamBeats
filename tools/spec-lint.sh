#!/usr/bin/env bash
# spec-lint (r0-solution-structure section 8, gate 6): a change under src/ must reference an Approved spec.
# Usage: tools/spec-lint.sh <base-ref> "<pull request title and body>"
set -euo pipefail
base="${1:?base ref}"
text="${2:-}"

changed=$(git diff --name-only "$base"...HEAD -- src/ | wc -l)
if [ "$changed" -eq 0 ]; then
  echo "spec-lint: no src/ changes."
  exit 0
fi

ids=$(printf '%s\n%s' "$text" "$(git log --format=%B "$base"..HEAD)" | grep -oE '\b(spec|specs/)[ -]?0[0-9]{2}\b|\b0[0-9]{2}-[a-z-]+\b' | grep -oE '0[0-9]{2}' | sort -u || true)
if [ -z "$ids" ]; then
  echo "spec-lint: src/ changed but no spec ID (for example 'spec 004') in the PR text or commits." >&2
  exit 1
fi

status=0
for id in $ids; do
  spec=$(ls -d specs/"$id"-*/spec.md 2>/dev/null | head -1 || true)
  if [ -z "$spec" ]; then
    echo "spec-lint: spec $id does not exist." >&2; status=1; continue
  fi
  if ! grep -qE '^\| *Status *\| *Approved *\|' "$spec"; then
    echo "spec-lint: $spec is not Approved." >&2; status=1
  else
    echo "spec-lint: $spec Approved."
  fi
done
exit $status
