-- Database roles (r0-solution-structure section 2). Run once per cluster by a DBA or the compose init.
-- Passwords are supplied by the caller through psql variables; nothing secret is stored here.
--   psql -v owner_password=... -v app_password=... -v operator_password=... -f bootstrap.sql
--
-- tb_owner    owns schemas and tables, runs migrations. Not a superuser.
-- tb_app      application role: DML only on granted tables, no ownership, no BYPASSRLS.
-- tb_operator break-glass role for audited platform operations (stub in R0, no grants).

select format('create role tb_owner login password %L nosuperuser nobypassrls nocreaterole', :'owner_password')
where not exists (select 1 from pg_roles where rolname = 'tb_owner') \gexec
select format('create role tb_app login password %L nosuperuser nobypassrls nocreaterole nocreatedb', :'app_password')
where not exists (select 1 from pg_roles where rolname = 'tb_app') \gexec
select format('create role tb_operator login password %L nosuperuser nobypassrls nocreaterole nocreatedb', :'operator_password')
where not exists (select 1 from pg_roles where rolname = 'tb_operator') \gexec

select 'create database teambeats owner tb_owner'
where not exists (select 1 from pg_database where datname = 'teambeats') \gexec

\connect teambeats
revoke all on schema public from public;
grant connect on database teambeats to tb_app;
-- pgvector for the search index (spec 012 plan). The extension is not trusted, so it is created here by the
-- cluster administrator in its own schema, and the owner and application roles may use its types and operators.
create schema if not exists extensions;
create extension if not exists vector schema extensions;
grant usage on schema extensions to tb_owner, tb_app;
