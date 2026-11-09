-- Spec 012 US4 (R1, FR-010): best bets and synonyms, managed by search administrators per locale. Both are product
-- data of the tenant (section 8: Internal, life of tenant). Queries and terms are stored normalized as the search box
-- normalizes them (SearchText.Normalize), so Arabic letter forms and case never decide whether a rule applies.

-- A best bet pins one item to the top of the results for a query. The item must be readable by the person searching,
-- checked at query time by its owner like every other result.
create table search.best_bet (
  tenant_id uuid not null,
  id uuid not null,
  query text not null check (length(query) between 1 and 200),
  locale text null check (locale is null or locale in ('ar', 'en')),
  source text not null check (length(source) between 1 and 40),
  item_id uuid not null,
  created_at timestamptz not null,
  created_by uuid not null,
  primary key (tenant_id, id)
);
create unique index best_bet_unique on search.best_bet (tenant_id, query, coalesce(locale, ''), source, item_id);
select platform.enable_tenant_rls('search.best_bet');
grant select, insert, delete on search.best_bet to tb_app;

-- A synonym set: words that find each other ("leave", "vacation", "اجازه"). A set with a locale applies to queries in
-- that language; a set without one applies to every query.
create table search.synonym_set (
  tenant_id uuid not null,
  id uuid not null,
  locale text null check (locale is null or locale in ('ar', 'en')),
  terms text[] not null check (cardinality(terms) between 2 and 20),
  updated_at timestamptz not null,
  updated_by uuid not null,
  primary key (tenant_id, id)
);
create index synonym_set_terms on search.synonym_set using gin (terms);
select platform.enable_tenant_rls('search.synonym_set');
grant select, insert, update, delete on search.synonym_set to tb_app;
