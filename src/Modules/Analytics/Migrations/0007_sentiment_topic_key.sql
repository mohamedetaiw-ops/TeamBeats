-- Spec 023 R3 T020 (US11 scenario 3, FR-033): a stable key for the topic a sentiment label belongs to, so a module can
-- ask for the trend of one of its own topics (a community) without matching on names. Null for older rows and for
-- sources that only have a display topic.
alter table analytics.sentiment add column topic_key text null check (topic_key is null or length(topic_key) between 1 and 100);
create index sentiment_topic_key on analytics.sentiment (tenant_id, topic_key, day) where topic_key is not null;
