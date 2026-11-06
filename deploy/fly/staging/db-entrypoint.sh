#!/bin/bash
# Starts PostgreSQL with the stock image entrypoint, then creates the TeamBeats roles and database (deploy/db/bootstrap.sql)
# and syncs their passwords from the Fly secrets. Both steps are idempotent and run on every start. Staging only.
set -euo pipefail
: "${TB_OWNER_PASSWORD:?}" "${TB_APP_PASSWORD:?}" "${TB_OPERATOR_PASSWORD:?}"

docker-entrypoint.sh postgres -c listen_addresses='*' &
pg=$!
trap 'kill -INT "$pg" 2>/dev/null; wait "$pg"' INT TERM

sync() {
  local vars=(-v ON_ERROR_STOP=1 -h /var/run/postgresql -U postgres -q
    -v owner_password="$TB_OWNER_PASSWORD" -v app_password="$TB_APP_PASSWORD" -v operator_password="$TB_OPERATOR_PASSWORD")
  gosu postgres psql "${vars[@]}" -f /teambeats/bootstrap.sql >/dev/null &&
  gosu postgres psql "${vars[@]}" -d teambeats -f /teambeats/sync-roles.sql >/dev/null
}

# The first boot runs initdb behind a temporary server, so retry until the real server accepts the sync.
synced=false
for _ in $(seq 1 120); do
  kill -0 "$pg" 2>/dev/null || exit 1
  if sync 2>/dev/null; then synced=true; echo "tb-entrypoint: roles and database are in place"; break; fi
  sleep 2
done
if [ "$synced" != true ]; then echo "tb-entrypoint: role sync failed:" >&2; sync || true; fi

wait "$pg"
