"use client";

import { useId, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { StateView } from "@/components/ui/StateView";
import type { OrgUnitView } from "@/lib/admin/types";
import { problemMessages } from "@/lib/notifications/types";
import type { JudgedQuery, JudgedSetSummary, JudgedSetView, Judgment, SearchPage, SearchResult } from "@/lib/search/types";
import "./search.css";

type Message = { error: boolean; text: string } | null;
type Draft = { query: string; locale: string; judgments: Judgment[] };

const api = "/api/v1/evaluation/search-sets";

function pick(names: Record<string, string>, locale: string): string {
  return names[locale] ?? names.en ?? Object.values(names)[0] ?? "";
}

/**
 * Judged query sets (spec 012 US8, FR-020): a search administrator lists queries with the results that should rank
 * high, graded 1 to 3, picked from what search finds now. Each set runs as an employee of its unit, and the retrieval
 * release gate scores every set (precision at 5, NDCG at 10) with the configuration in force and with the change. The
 * API keeps who may manage sets and refuses a save made over an older version.
 */
export function JudgedSets({ initial, units }: { initial: JudgedSetSummary[]; units: OrgUnitView[] }) {
  const t = useTranslations("search.admin.judged");
  const locale = useLocale();
  const format = useFormatter();
  const router = useRouter();
  const [name, setName] = useState("");
  const [unit, setUnit] = useState(units[0]?.id ?? "");
  const [open, setOpen] = useState<JudgedSetView | null>(null);
  const [queries, setQueries] = useState<Draft[]>([]);
  const [draft, setDraft] = useState<Draft>({ query: "", locale: "en", judgments: [] });
  const [found, setFound] = useState<SearchResult[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<Message>(null);
  const ids = { name: useId(), unit: useId(), query: useId(), locale: useId() };
  const unitName = (id: string) => pick(units.find((u) => u.id === id)?.names ?? {}, locale) || t("unknownUnit");

  async function call<T>(url: string, method: string, body?: unknown): Promise<T | null> {
    setBusy(true);
    setMessage(null);
    try {
      const response = await fetch(url, {
        method,
        headers: body === undefined ? undefined : { "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (response.ok) return (await response.json()) as T;
      setMessage({ error: true, text: (await problemMessages(response)).join(" ") || t("failed") });
    } catch {
      setMessage({ error: true, text: t("failed") });
    } finally {
      setBusy(false);
    }
    return null;
  }

  function edit(view: JudgedSetView) {
    setOpen(view);
    setQueries(view.queries.map((q: JudgedQuery) => ({ query: q.query, locale: q.locale, judgments: q.judgments })));
    setDraft({ query: "", locale: "en", judgments: [] });
    setFound(null);
  }

  async function create(e: FormEvent) {
    e.preventDefault();
    const view = await call<JudgedSetView>(api, "POST", { names: { [locale === "ar" ? "ar" : "en"]: name.trim() }, orgUnitId: unit });
    if (view) {
      setName("");
      setMessage({ error: false, text: t("created") });
      edit(view);
      router.refresh();
    }
  }

  async function openSet(id: string) {
    const view = await call<JudgedSetView>(`${api}/${encodeURIComponent(id)}`, "GET");
    if (view) edit(view);
  }

  async function find(e: FormEvent) {
    e.preventDefault();
    const page = await call<SearchPage>(`/api/v1/search?${new URLSearchParams({ q: draft.query.trim() })}`, "GET");
    if (page) setFound(page.results.slice(0, 10));
  }

  function grade(r: SearchResult, value: number) {
    setDraft((d) => {
      const others = d.judgments.filter((j) => !(j.source === r.source && j.itemId === r.itemId));
      return { ...d, judgments: value > 0 ? [...others, { source: r.source, itemId: r.itemId, grade: value }] : others };
    });
  }

  function addQuery() {
    setQueries((q) => [...q, { ...draft, query: draft.query.trim() }]);
    setDraft({ query: "", locale: draft.locale, judgments: [] });
    setFound(null);
  }

  async function save() {
    if (!open) return;
    const view = await call<JudgedSetView>(`${api}/${encodeURIComponent(open.id)}/queries`, "PUT", { version: open.version, queries });
    if (view) {
      edit(view);
      setMessage({ error: false, text: t("saved", { version: view.version }) });
      router.refresh();
    }
  }

  const setColumns: Column<JudgedSetSummary>[] = [
    {
      key: "name",
      header: t("name"),
      rowHeader: true,
      cell: (s) => (
        <Button variant="quiet" size="sm" onClick={() => void openSet(s.id)}>
          <bdi>{pick(s.names, locale)}</bdi>
        </Button>
      ),
    },
    { key: "unit", header: t("unit"), cell: (s) => <bdi>{unitName(s.orgUnitId)}</bdi> },
    { key: "queries", header: t("queries"), align: "end", cell: (s) => format.number(s.queries) },
    { key: "version", header: t("version"), align: "end", priority: "low", cell: (s) => format.number(s.version) },
  ];

  const queryColumns: Column<Draft & { at: number }>[] = [
    { key: "query", header: t("query"), rowHeader: true, cell: (q) => <bdi>{q.query}</bdi> },
    { key: "locale", header: t("locale"), cell: (q) => t(`locales.${q.locale === "ar" ? "ar" : "en"}`) },
    { key: "judged", header: t("judged"), align: "end", cell: (q) => format.number(q.judgments.length) },
    {
      key: "remove",
      header: t("actions"),
      align: "end",
      cell: (q) => (
        <Button variant="quiet" size="sm" aria-label={t("removeQuery", { query: q.query })} onClick={() => setQueries((all) => all.filter((_, i) => i !== q.at))}>
          {t("remove")}
        </Button>
      ),
    },
  ];

  const gradeOf = (r: SearchResult) => draft.judgments.find((j) => j.source === r.source && j.itemId === r.itemId)?.grade ?? 0;

  return (
    <section aria-labelledby="judged-title" className="search-judged">
      <h2 id="judged-title">{t("title")}</h2>
      <p>{t("intro")}</p>
      <DataTable
        caption={t("caption")}
        columns={setColumns}
        rows={initial}
        rowKey={(s) => s.id}
        emptyTitle={t("emptyTitle")}
        emptyMessage={t("empty")}
        density="compact"
      />
      {units.length > 0 ? (
        <form onSubmit={create}>
          <h3>{t("create")}</h3>
          <div className="field">
            <label htmlFor={ids.name}>{t("name")}</label>
            <input id={ids.name} value={name} onChange={(e) => setName(e.target.value)} maxLength={120} required />
          </div>
          <div className="field">
            <label htmlFor={ids.unit}>{t("unit")}</label>
            <p id={`${ids.unit}-hint`} className="field-hint">
              {t("unitHint")}
            </p>
            <select id={ids.unit} value={unit} onChange={(e) => setUnit(e.target.value)} aria-describedby={`${ids.unit}-hint`} required>
              {units.map((u) => (
                <option key={u.id} value={u.id}>
                  {pick(u.names, locale)}
                </option>
              ))}
            </select>
          </div>
          <div className="button-row">
            <Button type="submit" variant="secondary" busy={busy} busyLabel={t("busy")}>
              {t("createAction")}
            </Button>
          </div>
        </form>
      ) : (
        <StateView kind="denied" title={t("noUnitsTitle")} message={t("noUnits")} />
      )}

      {open ? (
        <div className="search-admin-change" data-testid="judged-set-editor">
          <h3>{t("editing", { name: pick(open.names, locale), version: open.version })}</h3>
          <DataTable
            caption={t("queriesCaption")}
            columns={queryColumns}
            rows={queries.map((q, at) => ({ ...q, at }))}
            rowKey={(q) => `${q.at}:${q.query}`}
            emptyTitle={t("noQueriesTitle")}
            emptyMessage={t("noQueries")}
            density="compact"
          />
          <form onSubmit={find}>
            <h4>{t("addQuery")}</h4>
            <div className="field">
              <label htmlFor={ids.query}>{t("query")}</label>
              <input id={ids.query} type="search" value={draft.query} onChange={(e) => setDraft({ ...draft, query: e.target.value })} maxLength={200} required />
            </div>
            <div className="field">
              <label htmlFor={ids.locale}>{t("locale")}</label>
              <select id={ids.locale} value={draft.locale} onChange={(e) => setDraft({ ...draft, locale: e.target.value })}>
                <option value="en">{t("locales.en")}</option>
                <option value="ar">{t("locales.ar")}</option>
              </select>
            </div>
            <div className="button-row">
              <Button type="submit" variant="secondary" busy={busy} busyLabel={t("busy")}>
                {t("find")}
              </Button>
            </div>
          </form>
          {found ? (
            found.length === 0 ? (
              <StateView kind="empty" icon="search" title={t("noResultsTitle")} message={t("noResults")} />
            ) : (
              <>
                <ol className="search-judged-results" aria-label={t("results")}>
                  {found.map((r) => (
                    <li key={`${r.source}:${r.itemId}`}>
                      <label>
                        <span>
                          <bdi>{r.title}</bdi>
                        </span>
                        <select
                          aria-label={t("gradeFor", { title: r.title })}
                          value={gradeOf(r)}
                          onChange={(e) => grade(r, Number(e.target.value))}
                        >
                          {[0, 1, 2, 3].map((g) => (
                            <option key={g} value={g}>
                              {t(`grades.${g}`)}
                            </option>
                          ))}
                        </select>
                      </label>
                    </li>
                  ))}
                </ol>
                <div className="button-row">
                  <Button variant="secondary" disabled={draft.judgments.length === 0} onClick={addQuery}>
                    {t("keepQuery")}
                  </Button>
                </div>
              </>
            )
          ) : null}
          <div className="button-row">
            <Button variant="primary" busy={busy} busyLabel={t("busy")} onClick={() => void save()}>
              {t("save")}
            </Button>
          </div>
        </div>
      ) : null}
      <div aria-live="polite">
        {message ? (
          <p className={message.error ? "field-error" : "notice"} role={message.error ? "alert" : "status"}>
            {message.text}
          </p>
        ) : null}
      </div>
    </section>
  );
}
