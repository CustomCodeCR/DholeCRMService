#!/usr/bin/env bash
# Provision only CRM's dedicated PostgreSQL database in the existing Dhole network.
# Does not modify any existing database, role, schema, service or server directory.
set -euo pipefail
umask 077
test -r "$DHOLE_ENV_FILE"
test -n "$DHOLE_NETWORK"

docker run --rm --network "$DHOLE_NETWORK" --env-file "$DHOLE_ENV_FILE" postgres:17-alpine sh -eu -c '
  : "$POSTGRES_USER" "$POSTGRES_PASSWORD"
  host="$(printenv POSTGRES_HOST || true)"
  port="$(printenv POSTGRES_PORT || true)"
  database="$(printenv CRM_DB || true)"
  test -n "$host" || host="postgres"
  test -n "$port" || port="5432"
  test -n "$database" || database="dhole_crm"
  printf "%s" "$database" | grep -Eq "^[a-zA-Z_][a-zA-Z_0-9]*$" || {
    echo "Invalid CRM database identifier" >&2
    exit 2
  }
  export PGPASSWORD="$POSTGRES_PASSWORD"
  existing="$(psql -w -X -qAt -v ON_ERROR_STOP=1 -h "$host" -p "$port" -U "$POSTGRES_USER" -d postgres -c "SELECT COUNT(*) FROM pg_database WHERE datname = '\''$database'\''")"
  if [ "$existing" = "0" ]; then
    echo "CRM PostgreSQL database does not exist; provisioning a dedicated empty database"
    createdb -w -h "$host" -p "$port" -U "$POSTGRES_USER" "$database"
  elif [ "$existing" != "1" ]; then
    echo "Could not verify CRM PostgreSQL database" >&2
    exit 1
  else
    echo "CRM PostgreSQL database already exists; leaving it unchanged"
  fi
  psql -w -X -qAt -v ON_ERROR_STOP=1 -h "$host" -p "$port" -U "$POSTGRES_USER" -d "$database" -c "SELECT 1" | grep -qx 1
  echo "CRM PostgreSQL connection verified"
'
