"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { locationName, type Location, type PersonCard, type SearchPage } from "@/lib/people/types";
import { PersonItem } from "./PersonItem";
import "./people.css";

type Query = { q: string; unitId: string; location: string };

/**
 * People search (spec 010 US2) and new joiners (US4). Search runs on the API, which applies the visibility policy and
 * leaves guests out; this screen sends the query and shows the page it gets back.
 */
export function PeopleDirectory({ units, locations, newcomers }: { units: OrgUnit[]; locations: Location[]; newcomers: PersonCard[] }) {
  const t = useTranslations("people");
  const locale = useLocale();
  const [query, setQuery] = useState<Query>({ q: "", unitId: "", location: "" });
  const [items, setItems] = useState<PersonCard[] | null>(null);
  const [next, setNext] = useState<number | null>(null);
  const [error, setError] = useState(false);
  const [busy, setBusy] = useState(false);

  async function load(current: Query, offset: number) {
    setBusy(true);
    setError(false);
    const params = new URLSearchParams();
    if (current.q.trim()) params.set("q", current.q.trim());
    if (current.unitId) params.set("unitId", current.unitId);
    if (current.location) params.set("location", current.location);
    if (offset > 0) params.set("offset", String(offset));
    try {
      const response = await fetch(`/api/v1/people?${params.toString()}`);
      if (!response.ok) {
        setError(true);
        return;
      }
      const page = (await response.json()) as SearchPage;
      setItems((before) => (offset > 0 && before ? [...before, ...page.items] : page.items));
      setNext(page.next);
    } catch {
      setError(true);
    } finally {
      setBusy(false);
    }
  }

  // Filters apply at once; the text query waits for the search button or Enter.
  function filter(change: Partial<Query>) {
    const next = { ...query, ...change };
    setQuery(next);
    void load(next, 0);
  }

  return (
    <div className="people">
      <section aria-labelledby="people-search-title" className="people-section">
        <h2 id="people-search-title">{t("search.label")}</h2>
        <form
          role="search"
          aria-labelledby="people-search-title"
          className="people-search"
          onSubmit={(e) => {
            e.preventDefault();
            void load(query, 0);
          }}
        >
          <FormField
            label={t("search.label")}
            name="q"
            type="search"
            hint={t("search.hint")}
            value={query.q}
            onChange={(e) => setQuery({ ...query, q: e.target.value })}
          />
          <label className="field people-select">
            <span>{t("search.unit")}</span>
            <select value={query.unitId} onChange={(e) => filter({ unitId: e.target.value })}>
              <option value="">{t("search.any")}</option>
              {units.map((u) => (
                <option key={u.id} value={u.id}>
                  {unitName(u, locale)}
                </option>
              ))}
            </select>
          </label>
          <label className="field people-select">
            <span>{t("search.location")}</span>
            <select value={query.location} onChange={(e) => filter({ location: e.target.value })}>
              <option value="">{t("search.any")}</option>
              {locations.map((l) => (
                <option key={l.key} value={l.key}>
                  {locationName(l, locale)}
                </option>
              ))}
            </select>
          </label>
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("loading")}>
            {t("search.submit")}
          </Button>
        </form>
        <div aria-live="polite">
          {error ? <p className="notice notice-error">{t("failed")}</p> : null}
          {items && items.length === 0 && !error ? <p>{t("search.none")}</p> : null}
        </div>
        {items && items.length > 0 ? (
          <>
            <h3>{t("search.results")}</h3>
            <ul className="people-list" data-testid="people-results">
              {items.map((p) => (
                <li key={p.id}>
                  <PersonItem person={p} locale={locale} />
                </li>
              ))}
            </ul>
            {next !== null ? (
              <Button variant="secondary" busy={busy} busyLabel={t("loading")} onClick={() => void load(query, next)}>
                {t("search.more")}
              </Button>
            ) : null}
          </>
        ) : null}
      </section>

      <section aria-labelledby="people-newcomers-title" className="people-section">
        <h2 id="people-newcomers-title">{t("newcomers.title")}</h2>
        {newcomers.length === 0 ? (
          <p>{t("newcomers.none")}</p>
        ) : (
          <ul className="people-list" data-testid="people-newcomers">
            {newcomers.map((p) => (
              <li key={p.id}>
                <PersonItem person={p} locale={locale} />
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
