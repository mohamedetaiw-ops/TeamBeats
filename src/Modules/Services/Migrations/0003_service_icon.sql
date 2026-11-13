-- Spec 016 FR-018 and spec 036 DLS-081: each service has its own icon from the icon set, chosen by the owner. Null
-- means the default of the service's category (module services take the icon their module registers).
alter table services.service add column icon text null check (icon is null or icon ~ '^[a-z][a-z0-9-]{1,31}$');
