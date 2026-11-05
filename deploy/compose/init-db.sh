#!/bin/sh
# Creates the TeamBeats roles and database with development passwords (matches src/Api.DevHost/appsettings.Development.json).
set -e
psql -v ON_ERROR_STOP=1 -U postgres \
  -v owner_password=tb_owner_dev_only -v app_password=tb_app_dev_only -v operator_password=tb_operator_dev_only \
  -f /teambeats/bootstrap.sql
