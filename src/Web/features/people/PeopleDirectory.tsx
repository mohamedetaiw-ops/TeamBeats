"use client";

import { useEffect, useMemo, useState } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { StateView } from "@/components/ui/StateView";
import { FilterToolbar, ToolbarSearch, ToolbarSelect } from "@/components/ui/FilterToolbar";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { locationName, type Location, type PersonCard, type SearchPage } from "@/lib/people/types";
import { PersonItem } from "./PersonItem";
import "./people.css";

type Query = { q: string; unitId: string; location: string };

/**
 * Units in tree order with an indent per level, and the parent's name after any name another unit also has, so no two
 * entries of the filter read the same (spec 010 FR-023).
 */
export function unitOptions(units: readonly OrgUnit[], locale: string): { id: string; label: string; depth: number }[] {
  const byId = new Map(units.map((u) => [u.id, u]));
  const children = new Map<string | null, OrgUnit[]>();
  for (const u of units) {
    const parent = u.parentId && byId.has(u.parentId) ? u.parentId : null;
    children.set(parent, [...(children.get(parent) ?? []), u]);
  }
  const counts = new Map<string, number>();
  for (const u of units) counts.set(unitName(u, locale), (counts.get(unitName(u, locale)) ?? 0) + 1);
  const out: { id: string; label: string; depth: number }[] = [];
  const walk = (parent: string | null, depth: number) => {
    const list = [...(children.get(parent) ?? [])].sort((a, b) => unitName(a, locale).localeCompare(unitName(b, locale), locale));
    for (const u of list) {
      const name = unitName(u, locale);
      const up = u.parentId ? byId.get(u.parentId) : undefined;
      out.push({ id: u.id, label: (counts.get(name) ?? 0) > 1 && up ? `${name}, ${unitName(up, locale)}` : name, depth });
      walk(u.id, depth + 1);
    }
  };
  walk(null, 0);
  return out;
}

function fromParams(params: URLSearchParams): Query {
  return { q: params.get("q") ?? "", unitId: params.get("unit") ?? "", location: params.get("location") ?? "" };
}

/**
 * People search (spec 010 US2) and new joiners (US4). Search runs on the API, which applies the visibility policy and
 * leaves guests out; this screen sends the query and shows the page it gets back.
 */
export function PeopleDirectory({ units, locations, newcomers }: { units: OrgUnit[]; locations: Location[]; newcomers: PersonCard[] }) {
  const t = useTranslations("people");
  const locale = useLocale();
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  // The search and filters live in the address, so a reload or a shared link opens the same results (spec 008 FR-065).
  const [query, setQuery] = useState<Query>(() => fromParams(new URLSearchParams(searchParams.toString())));
  const options = useMemo(() => unitOptions(units, locale), [units, locale]);
  const [items, setItems] = useState<PersonCard[] | null>(null);
  const [next, setNext] = useState<number | null>(null);
  const [error, setError] = useState(false);
  const [busy, setBusy] = useState(false);
  const [layout, setLayout] = useState("grid");

  async function load(current: Query, offset: number) {
    if (offset === 0) {
      const address = new URLSearchParams();
      if (current.q.trim()) address.set("q", current.q.trim());
      if (current.unitId) address.set("unit", current.unitId);
      if (current.location) address.set("location", current.location);
      const text = address.toString();
      router.replace(text ? `${pathname}?${text}` : pathname, { scroll: false });
    }
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

  // A link with a search runs it on arrival; nothing runs for a plain visit.
  useEffect(() => {
    const initial = fromParams(new URLSearchParams(window.location.search));
    if (!(initial.q || initial.unitId || initial.location)) return;
    const timer = window.setTimeout(() => void load(initial, 0), 0);
    return () => window.clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // An empty result offers one next step: start again with no search and no filters (DLS-087).
  function clear() {
    setQuery({ q: "", unitId: "", location: "" });
    setItems(null);
    setNext(null);
    router.replace(pathname, { scroll: false });
  }

  // Filters apply at once; the text query waits for the search button or Enter.
  function filter(change: Partial<Query>) {
    const next = { ...query, ...change };
    setQuery(next);
    void load(next, 0);
  }

  const unitLabel = options.find((u) => u.id === query.unitId)?.label;
  const chosenLocation = locations.find((l) => l.key === query.location);
  const locationLabel = chosenLocation ? locationName(chosenLocation, locale) : undefined;

  return (
    <div className="people">
      <section aria-labelledby="people-search-title" className="people-block">
        <h2 id="people-search-title" className="visually-hidden">
          {t("search.label")}
        </h2>
        <FilterToolbar
          label={t("search.label")}
          onSubmit={(e) => {
            e.preventDefault();
            void load(query, 0);
          }}
          search={<ToolbarSearch label={t("search.label")} name="q" placeholder={t("search.hint")} value={query.q} onChange={(e) => setQuery({ ...query, q: e.target.value })} />}
          end={
            <>
              <Button type="submit" variant="secondary" busy={busy} busyLabel={t("loading")}>
                {t("search.submit")}
              </Button>
              <SegmentedControl
                label={t("layout.label")}
                name="people-layout"
                value={layout}
                onChange={setLayout}
                options={[
                  { value: "grid", label: t("layout.grid") },
                  { value: "list", label: t("layout.list") },
                ]}
              />
            </>
          }
          applied={[
            ...(unitLabel ? [{ key: "unit", label: `${t("search.unit")}: ${unitLabel}`, onRemove: () => filter({ unitId: "" }) }] : []),
            ...(locationLabel ? [{ key: "location", label: `${t("search.location")}: ${locationLabel}`, onRemove: () => filter({ location: "" }) }] : []),
          ]}
          onClear={clear}
        >
          <ToolbarSelect label={t("search.unit")} value={query.unitId} onChange={(e) => filter({ unitId: e.target.value })}>
            <option value="">{t("search.any")}</option>
            {options.map((u) => (
              <option key={u.id} value={u.id}>
                {"\u00a0\u00a0".repeat(u.depth)}
                {u.label}
              </option>
            ))}
          </ToolbarSelect>
          <ToolbarSelect label={t("search.location")} value={query.location} onChange={(e) => filter({ location: e.target.value })}>
            <option value="">{t("search.any")}</option>
            {locations.map((l) => (
              <option key={l.key} value={l.key}>
                {locationName(l, locale)}
              </option>
            ))}
          </ToolbarSelect>
        </FilterToolbar>
        <div aria-live="polite">
          {error ? <p className="notice notice-error">{t("failed")}</p> : null}
          {items && items.length === 0 && !error ? (
            <StateView
              kind="empty"
              icon="search"
              title={t("search.none")}
              message={t("search.noneHint")}
              action={
                <Button variant="secondary" onClick={clear}>
                  {t("search.clear")}
                </Button>
              }
            />
          ) : null}
        </div>
        {items && items.length > 0 ? (
          <>
            <h3>{t("search.results")}</h3>
            <ul className={layout === "grid" ? "people-list people-cards" : "people-list people-rows"} data-testid="people-results">
              {items.map((p) => (
                <li key={p.id}>
                  <PersonItem person={p} locale={locale} form={layout === "grid" ? "card" : "row"} />
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

      {/* New joiners show only without a search, so results stand alone (spec 010 FR-024). */}
      {items === null ? (
      <section aria-labelledby="people-newcomers-title" className="people-block">
        <h2 id="people-newcomers-title">{t("newcomers.title")}</h2>
        {newcomers.length === 0 ? (
          <p>{t("newcomers.none")}</p>
        ) : (
          <ul className={layout === "grid" ? "people-list people-cards" : "people-list people-rows"} data-testid="people-newcomers">
            {newcomers.map((p) => (
              <li key={p.id}>
                <PersonItem person={p} locale={locale} form={layout === "grid" ? "card" : "row"} />
              </li>
            ))}
          </ul>
        )}
      </section>
      ) : null}
    </div>
  );
}
