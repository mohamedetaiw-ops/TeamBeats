-- People who may never be candidates of an instance besides its starter (spec 016: nobody approves their own request,
-- including a request someone filed for them).
alter table workflow.process_instance add column excluded_user_ids uuid[] not null default '{}';
