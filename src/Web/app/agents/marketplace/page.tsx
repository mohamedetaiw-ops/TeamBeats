import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  AGENT_CATEGORIES,
  pickText,
  type MarketplaceView,
} from "@/lib/agents/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { TileCard } from "@/components/ui/TileCard";
import { Badge } from "@/components/ui/Badge";
import { ButtonLink } from "@/components/ui/Button";
import {
  FilterToolbar,
  ToolbarSearch,
  ToolbarSelect,
} from "@/components/ui/FilterToolbar";
import { MarketplaceActions } from "@/features/agents/MarketplaceActions";
import "@/features/agents/agents.css";

/**
 * The agent marketplace (spec 020 US6, FR-010): agents the person may use and those their owners offer on request, by
 * category and department, searchable. The API decides what is listed and what each person may do; agents hidden
 * outside their audience never reach this page.
 */
export default async function AgentMarketplacePage({
  searchParams,
}: {
  searchParams: Promise<{ q?: string; category?: string }>;
}) {
  const params = await searchParams;
  const q = (params.q ?? "").trim().slice(0, 200);
  const category = AGENT_CATEGORIES.find((c) => c === params.category);
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) redirect("/signin");
  const query = new URLSearchParams();
  if (q) query.set("q", q);
  if (category) query.set("category", category);
  const view = await apiGet<MarketplaceView>(
    `/api/v1/agents/marketplace${query.size > 0 ? `?${query}` : ""}`,
  );
  if (!view.ok) notFound();
  const t = await getTranslations("agents.marketplace");
  const tc = await getTranslations("agents.categories");
  const tu = await getTranslations("agents.use");
  const locale = await getLocale();
  const format = await getFormatter();
  const href = (keep: { q?: string; category?: string }) => {
    const next = new URLSearchParams();
    if (keep.q) next.set("q", keep.q);
    if (keep.category) next.set("category", keep.category);
    return next.size > 0
      ? `/agents/marketplace?${next}`
      : "/agents/marketplace";
  };
  const filtered = Boolean(q || category);
  const agents = view.data.agents;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: tu("title"), href: "/agents" },
          { label: t("title"), href: "/agents/marketplace" },
        ]}
      />
      <main id="main" className="page">
        <PageHeader
          title={t("title")}
          description={t("intro")}
          icon="spark"
          actions={<ButtonLink href="/agents">{t("mine")}</ButtonLink>}
        />
        {view.data.paused ? <p className="notice">{t("paused")}</p> : null}
        <FilterToolbar
          label={t("search")}
          method="get"
          search={
            <ToolbarSearch
              label={t("search")}
              name="q"
              placeholder={t("searchHint")}
              defaultValue={q}
            />
          }
          end={
            <button type="submit" className="button button-secondary">
              {t("apply")}
            </button>
          }
          applied={[
            ...(q ? [{ key: "q", label: q, href: href({ category }) }] : []),
            ...(category
              ? [{ key: "category", label: tc(category), href: href({ q }) }]
              : []),
          ]}
          clearHref="/agents/marketplace"
        >
          <ToolbarSelect
            label={t("category")}
            hideLabel
            name="category"
            defaultValue={category ?? ""}
          >
            <option value="">{t("anyCategory")}</option>
            {AGENT_CATEGORIES.map((c) => (
              <option key={c} value={c}>
                {tc(c)}
              </option>
            ))}
          </ToolbarSelect>
        </FilterToolbar>
        {agents.length === 0 ? (
          filtered ? (
            <StateView
              kind="empty"
              icon="search"
              title={t("noMatch")}
              message={t("noMatchHint")}
              action={
                <ButtonLink href="/agents/marketplace">{t("clear")}</ButtonLink>
              }
            />
          ) : (
            <StateView
              kind="empty"
              icon="spark"
              title={t("empty")}
              message={t("emptyHint")}
            />
          )
        ) : (
          <ul className="ui-tiles" data-testid="agents-marketplace">
            {agents.map((a) => {
              const name = pickText(a.name, locale);
              return (
                <li key={a.id} id={`agent-${a.id}`}>
                  <TileCard
                    title={name}
                    href={a.canUse ? `/agents/${a.id}` : `#agent-${a.id}`}
                    icon="spark"
                    testId="marketplace-agent"
                    meta={
                      <>
                        <bdi>{pickText(a.department, locale)}</bdi>
                        {" · "}
                        {a.rating === null
                          ? t("noRatings")
                          : t("rating", {
                              rating: format.number(a.rating, {
                                maximumFractionDigits: 1,
                                minimumFractionDigits: 1,
                              }),
                              count: a.ratings,
                            })}
                      </>
                    }
                    footer={
                      <>
                        {a.category ? (
                          <Badge tone="accent">{tc(a.category)}</Badge>
                        ) : null}
                        {a.favourite ? (
                          <Badge tone="info" icon="star">
                            {t("favourited")}
                          </Badge>
                        ) : null}
                        <MarketplaceActions agent={a} name={name} />
                      </>
                    }
                  >
                    <p>{pickText(a.description, locale)}</p>
                  </TileCard>
                </li>
              );
            })}
          </ul>
        )}
      </main>
    </>
  );
}
