# Staging PostgreSQL for Fly.io (PostgreSQL 18 with pgvector, as in deploy/compose). Staging only.
# Passwords come from Fly secrets set by .github/workflows/deploy-staging.yml, never from source control:
#   POSTGRES_PASSWORD, TB_OWNER_PASSWORD, TB_APP_PASSWORD, TB_OPERATOR_PASSWORD
FROM pgvector/pgvector:pg18
COPY deploy/db/bootstrap.sql /teambeats/bootstrap.sql
COPY deploy/fly/staging/db-sync-roles.sql /teambeats/sync-roles.sql
COPY deploy/fly/staging/db-entrypoint.sh /usr/local/bin/tb-entrypoint.sh
RUN chmod 0755 /usr/local/bin/tb-entrypoint.sh
ENTRYPOINT ["tb-entrypoint.sh"]
