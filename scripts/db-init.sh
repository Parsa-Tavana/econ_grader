#!/usr/bin/env sh
# Runs ONCE inside a throwaway mssql container (compose service `db-init`).
# Creates the EconGrader database if missing and the least-privilege
# econgrader_app login/user. Idempotent; safe on every boot.
set -eu

for i in $(seq 1 60); do
  /opt/mssql-tools18/bin/sqlcmd -C -S db -U sa -P "$SA_PASSWORD" -Q "SELECT 1" >/dev/null 2>&1 && break
  sleep 2
done

sqlcmd() { /opt/mssql-tools18/bin/sqlcmd -C -S db -U sa -P "$SA_PASSWORD" "$@"; }

# Database itself (EF would create it, but only AFTER it can authenticate,
# so create it here first).
sqlcmd -b -Q "IF DB_ID('EconGrader') IS NULL CREATE DATABASE [EconGrader];"

# Login is server-scoped: not part of any backup, recreated every fresh boot.
sqlcmd -b -v AppPassword="$DB_APP_PASSWORD" -i /init-db.sql

echo "db-init done"
