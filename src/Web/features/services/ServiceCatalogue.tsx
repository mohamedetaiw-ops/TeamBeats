"use client";

import Link from "next/link";
import { useMemo, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import { serviceLook } from "@/lib/services/look";
import { pick, type CatalogueView, type ServiceCard } from "@/lib/services/types";
import "./services.css";


/**
 * The service catalogue (spec 016 US1): the services the API offers this person, by category, with search,
 * favourites and recent use. Linked apps open in a new tab with an external cue and say whether sign-in is single
 * sign-on. Search runs on the API so it folds Arabic letter forms the same way everywhere.
 */
export function ServiceCatalogue({ initial }: { initial: CatalogueView }) {
  const t = useTranslations("services.catalogue");
  const locale = useLocale();
  const [view, setView] = useState(initial);
  const [query, setQuery] = useState("");
  const [category, setCategory] = useState("");
  const [state, setState] = useState<"ready" | "loading" | "failed">("ready");

  async function load(q: string, c: string) {
    setState("loading");
    try {
      const params = new URLSearchParams();
      if (q.trim()) params.set("q", q.trim());
      if (c) params.set("category", c);
      const response = await fetch(`/api/v1/services?${params.toString()}`, { cache: "no-store" });
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

  const favourites = view.services.filter((s) => s.favourite);
  const recent = useMemo(
    () => view.recent.map((id) => view.services.find((s) => s.id === id)).filter((s): s is ServiceCard => Boolean(s)),
    [view],
  );
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
                  <a className="ui-tile-link" href={s.url} target="_blank" rel="noopener noreferrer" onClick={() => void fetch(`/api/v1/services/${s.id}/used`, { method: "POST" })}>
                    <bdi>{name}</bdi>
                    <span className="visually-hidden">{t("external")}</span>
                  </a>
                ) : (
                  <Link className="ui-tile-link" href={s.kind === "module" && s.launchRoute ? s.launchRoute : `/services/${s.id}`}>
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
        </article>
      </li>
    );
  };

  return (
    <div className="services">
      <form
        role="search"
        className="ui-filter-bar"
        onSubmit={(e) => {
          e.preventDefault();
          void load(query, category);
        }}
      >
        <FormField label={t("search")} name="q" type="search" placeholder={t("searchHint")} value={query} onChange={(e) => setQuery(e.target.value)} />
        <label className="field">
          <span>{t("category")}</span>
          <select
            value={category}
            onChange={(e) => {
              setCategory(e.target.value);
              void load(query, e.target.value);
            }}
          >
            <option value="">{t("allCategories")}</option>
            {view.categories.map((c) => (
              <option key={c.id} value={c.id}>
                {pick(c.names, locale)}
              </option>
            ))}
          </select>
        </label>
        <Button type="submit" variant="primary" busy={state === "loading"} busyLabel={t("searching")}>
          {t("submit")}
        </Button>
      </form>

      {state === "failed" ? <StateView kind="error" title={t("failed")} message="" /> : null}

      {favourites.length > 0 ? (
        <section className="services-block" aria-labelledby="services-favourites">
          <h2 id="services-favourites">{t("favourites")}</h2>
          <ul className="ui-tiles">{favourites.map(card)}</ul>
        </section>
      ) : null}

      {recent.length > 0 ? (
        <section className="services-block" aria-labelledby="services-recent">
          <h2 id="services-recent">{t("recent")}</h2>
          <ul className="ui-tiles">{recent.map(card)}</ul>
        </section>
      ) : null}

      <div data-testid="services-results" className="services">
        {groups.length === 0 ? (
          <StateView kind="empty" title={t("none")} message="" />
        ) : (
          groups.map((g) => (
            <section key={g.id} className="services-block" aria-labelledby={`services-category-${g.id}`}>
              <h2 id={`services-category-${g.id}`}>
                <bdi>{g.name}</bdi>
              </h2>
              <ul className="ui-tiles">{g.services.map(card)}</ul>
            </section>
          ))
        )}
      </div>
    </div>
  );
}
