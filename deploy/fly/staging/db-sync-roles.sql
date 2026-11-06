-- Keeps the TeamBeats role passwords equal to the current Fly secrets (staging only). Runs after bootstrap.sql on every
-- start, so rotating the secrets and restarting the machine is enough. Passwords arrive as psql variables.
select format('alter role tb_owner password %L', :'owner_password') \gexec
select format('alter role tb_app password %L', :'app_password') \gexec
select format('alter role tb_operator password %L', :'operator_password') \gexec
