"use client";

import Link from "next/link";
import { useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FilterToolbar, ToolbarSearch } from "@/components/ui/FilterToolbar";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import { serviceLook } from "@/lib/services/look";
import { pick, type CatalogueView, type ServiceCard } from "@/lib/services/types";
import "./services.css";


/**
 * The service catalogue (spec 016 US1, FR-023): search first, then the person's own services (favourites and recently
 * used as one compact strip, no service twice), then categories as tabs with counts so no category sits under another,
 * in a grid or a list. A card says what happens next. Linked apps open in a new tab with an external cue and say
 * whether sign-in is single sign-on. Search runs on the API so it folds Arabic letter forms the same way everywhere.
 */
export function ServiceCatalogue({ initial, initialQuery = "", initialCategory = "" }: { initial: CatalogueView; initialQuery?: string; initialCategory?: string }) {
  const t = useTranslations("services.catalogue");
  const status = useTranslations("services.status");
  const locale = useLocale();
  const [view, setView] = useState(initial);
  const router = useRouter();
  const [query, setQuery] = useState(initialQuery);
  const [category, setCategory] = useState(initialCategory);
  const [state, setState] = useState<"ready" | "loading" | "failed">("ready");
  const [layout, setLayout] = useState("grid");

  const address = (q: string, c: string) => {
    const params = new URLSearchParams();
    if (q.trim()) params.set("q", q.trim());
    if (c) params.set("category", c);
    router.replace(params.size ? `/services?${params.toString()}` : "/services", { scroll: false });
  };

  async function load(q: string, c: string) {
    setState("loading");
    try {
      address(q, c);
      const response = await fetch(`/api/v1/services${q.trim() ? `?q=${encodeURIComponent(q.trim())}` : ""}`, { cache: "no-store" });
      if (!response.ok) {
        setState("failed");
        return;
      }
      setView((await response.json()) as CatalogueView);
      setState("ready");
    } catch {
      setState("failed");
    }
  }

  async function toggleFavourite(service: ServiceCard) {
    const response = await fetch(`/api/v1/services/${service.id}/favourite`, { method: service.favourite ? "DELETE" : "PUT" });
    if (response.ok) {
      setView({ ...view, services: view.services.map((s) => (s.id === service.id ? { ...s, favourite: !s.favourite } : s)) });
    }
  }

  // The person's own services: favourites, then what they used lately, each service once.
  const mine = useMemo(() => {
    const favourites = view.services.filter((s) => s.favourite);
    const recent = view.recent.map((id) => view.services.find((s) => s.id === id)).filter((s): s is ServiceCard => Boolean(s) && !s!.favourite);
    return [...favourites, ...recent].slice(0, 8);
  }, [view]);
  const groups = useMemo(() => {
    const byCategory = view.categories
      .map((c) => ({ id: c.id, name: pick(c.names, locale), services: view.services.filter((s) => s.categoryId === c.id) }))
      .filter((g) => g.services.length > 0);
    const other = view.services.filter((s) => !s.categoryId || !view.categories.some((c) => c.id === s.categoryId));
    return other.length > 0 ? [...byCategory, { id: "other", name: t("other"), services: other }] : byCategory;
  }, [view, locale, t]);

  // Each category keeps one icon and tint, so a service looks the same in Favourites, Recently used and its category.
  const look = (s: ServiceCard) => serviceLook(s, view.categories);

  const card = (s: ServiceCard) => {
    const name = pick(s.names, locale);
    const { icon, tone } = look(s);
    const description = pick(s.descriptions, locale);
    return (
      <li key={s.id}>
        <article className="ui-tile services-tile">
          <div className="ui-tile-head">
            <span className="ui-icon-tile" data-tone={tone}>
              <Icon name={s.kind === "linked" ? "external" : icon} size={22} mirrorInRtl={false} />
            </span>
            <div className="ui-tile-text">
              <h3 className="ui-tile-title">
                {s.kind === "linked" && s.url ? (
                  <a className="ui-tile-link" href={s.url} target="_blank" rel="noopener noreferrer" onClick={() => used(s)}>
                    <bdi>{name}</bdi>
                    <span className="visually-hidden">{t("external")}</span>
                  </a>
                ) : (
                  <Link className="ui-tile-link" href={href(s)}>
                    <bdi>{name}</bdi>
                  </Link>
                )}
              </h3>
              {s.kind === "linked" ? <p className="ui-tile-meta">{s.sso ? t("sso") : t("ownSignIn")}</p> : null}
            </div>
            <button
              type="button"
              className="services-favourite"
              aria-pressed={s.favourite}
              aria-label={t(s.favourite ? "unfavourite" : "favourite", { name })}
              title={s.favourite ? t("favourited") : t("addFavourite")}
              onClick={() => void toggleFavourite(s)}
            >
              <Icon name="star" size={20} />
            </button>
          </div>
          {description ? <p>{description}</p> : null}
          {/* What helps the choice before opening (spec 016 FR-019): who approves, the usual time, the last request. */}
          {s.approver ? (
            <p className="ui-tile-meta services-facts">
              <span>{t(`approver.${s.approver}`)}</span>
              {s.dueWorkingDays ? <span>{t("usualDays", { count: s.dueWorkingDays })}</span> : null}
            </p>
          ) : null}
          {s.latest ? (
            <p className="ui-tile-meta services-latest">
              <Link href={`/requests/${s.latest.id}`}>{t("latest", { number: s.latest.number, status: status(s.latest.status) })}</Link>
            </p>
          ) : null}
          {/* What opening the card does, said on the card (FR-023); the title is the link, so this is not a second one. */}
          <p className="services-next" aria-hidden="true">
            {t(next(s))}
            <Icon name={s.kind === "linked" ? "external" : "arrow-forward"} size={14} />
          </p>
        </article>
      </li>
    );
  };

  const next = (s: ServiceCard) => (s.kind === "form" ? "next.form" : s.kind === "linked" ? "next.linked" : "next.module");

  // The list view: one dense row per service with the same link, facts and favourite toggle as the card.
  const row = (s: ServiceCard) => {
    const name = pick(s.names, locale);
    const { icon, tone } = look(s);
    const description = pick(s.descriptions, locale);
    return (
      <li key={s.id} className="ui-row">
        <span className="ui-icon-tile" data-tone={tone}>
          <Icon name={s.kind === "linked" ? "external" : icon} size={20} mirrorInRtl={false} />
        </span>
        <div className="ui-row-text">
          {s.kind === "linked" && s.url ? (
            <a className="ui-row-link" href={s.url} target="_blank" rel="noopener noreferrer" onClick={() => used(s)}>
              <bdi>{name}</bdi>
              <span className="visually-hidden">{t("external")}</span>
            </a>
          ) : (
            <Link className="ui-row-link" href={href(s)}>
              <bdi>{name}</bdi>
            </Link>
          )}
          {description ? <span className="services-meta">{description}</span> : null}
        </div>
        <span className="services-meta services-row-next">{s.approver ? t(`approver.${s.approver}`) : t(next(s))}</span>
        <button
          type="button"
          className="services-favourite"
          aria-pressed={s.favourite}
          aria-label={t(s.favourite ? "unfavourite" : "favourite", { name })}
          title={s.favourite ? t("favourited") : t("addFavourite")}
          onClick={() => void toggleFavourite(s)}
        >
          <Icon name="star" size={20} />
        </button>
      </li>
    );
  };

  const pickCategory = (id: string) => {
    setCategory(id);
    address(query, id);
  };
  const shown = category && groups.some((g) => g.id === category) ? groups.filter((g) => g.id === category) : groups;
  const href = (s: ServiceCard) => (s.kind === "module" && s.launchRoute ? s.launchRoute : `/services/${s.id}`);
  const used = (s: ServiceCard) => void fetch(`/api/v1/services/${s.id}/used`, { method: "POST" });

  return (
    <div className="services">
      <FilterToolbar
        label={t("search")}
        onSubmit={(e) => {
          e.preventDefault();
          void load(query, category);
        }}
        search={<ToolbarSearch label={t("search")} name="q" placeholder={t("searchHint")} value={query} onChange={(e) => setQuery(e.target.value)} />}
        end={
          <>
            <Button type="submit" variant="secondary" busy={state === "loading"} busyLabel={t("searching")}>
              {t("submit")}
            </Button>
            <SegmentedControl
              label={t("layout.label")}
              name="services-layout"
              value={layout}
              onChange={setLayout}
              options={[
                { value: "grid", label: t("layout.grid") },
                { value: "list", label: t("layout.list") },
              ]}
            />
          </>
        }
      />

      {state === "failed" ? <StateView kind="error" title={t("failed")} message="" /> : null}

      {mine.length > 0 && !query.trim() ? (
        <section className="services-mine" aria-labelledby="services-mine">
          <h2 id="services-mine">{t("mine")}</h2>
          <ul>
            {mine.map((s) => {
              const name = pick(s.names, locale);
              const { icon, tone } = look(s);
              const inner = (
                <>
                  <span className="services-shortcut-icon" data-tone={tone}>
                    <Icon name={s.kind === "linked" ? "external" : icon} size={16} mirrorInRtl={false} />
                  </span>
                  <bdi>{name}</bdi>
                  {s.favourite ? <Icon name="star" size={14} label={t("favourited")} /> : null}
                </>
              );
              return (
                <li key={s.id}>
                  {s.kind === "linked" && s.url ? (
                    <a className="services-shortcut" href={s.url} target="_blank" rel="noopener noreferrer" onClick={() => used(s)}>
                      {inner}
                      <span className="visually-hidden">{t("external")}</span>
                    </a>
                  ) : (
                    <Link className="services-shortcut" href={href(s)}>
                      {inner}
                    </Link>
                  )}
                </li>
              );
            })}
          </ul>
        </section>
      ) : null}

      {groups.length > 1 ? (
        <div role="group" aria-label={t("category")} className="ui-tablist">
          <button type="button" className="ui-tab" aria-pressed={shown.length === groups.length} onClick={() => pickCategory("")}>
            {t("allCount", { count: view.services.length })}
          </button>
          {groups.map((g) => (
            <button key={g.id} type="button" className="ui-tab" aria-pressed={shown.length === 1 && shown[0]!.id === g.id} onClick={() => pickCategory(g.id)}>
              <bdi>{g.name}</bdi> <span className="services-count">{g.services.length}</span>
            </button>
          ))}
        </div>
      ) : null}

      <div data-testid="services-results" className="services">
        {groups.length === 0 ? (
          <StateView
            kind="empty"
            icon="search"
            title={t("none")}
            message={query || category ? t("noneHint") : ""}
            action={
              query || category ? (
                <Button
                  variant="secondary"
                  onClick={() => {
                    setQuery("");
                    setCategory("");
                    void load("", "");
                  }}
                >
                  {t("clear")}
                </Button>
              ) : undefined
            }
          />
        ) : (
          shown.map((g) => (
            <section key={g.id} className="services-block" aria-labelledby={`services-category-${g.id}`}>
              <h2 id={`services-category-${g.id}`} className={shown.length === 1 && groups.length > 1 ? "visually-hidden" : undefined}>
                <bdi>{g.name}</bdi>
              </h2>
              {layout === "list" ? <ul className="ui-rows">{g.services.map(row)}</ul> : <ul className="ui-tiles">{g.services.map(card)}</ul>}
            </section>
          ))
        )}
      </div>
    </div>
  );
}
