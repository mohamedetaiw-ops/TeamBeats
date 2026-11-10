"use client";

import Link from "next/link";
import { useEffect, useId, useMemo, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { dateTimeFull, relativeTime } from "@/lib/format";
import { problemMessages } from "@/lib/notifications/types";
import {
  documentHref,
  nameIn,
  type FolderView,
  type ItemPage,
  type LibraryView,
  type SavedView,
} from "@/lib/documents/types";
import { Button } from "@/components/ui/Button";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import { UploadDialog } from "./UploadDialog";
import "./documents.css";

type Answer = "loading" | "failed" | ItemPage;

/**
 * The library view (spec 011 US1 scenarios 4 and 5): the documents of a folder, narrowed by type, words and the type's
 * fields, sorted, and saved as a view people can share. The API filters what the viewer may see, so the list shows only
 * what comes back.
 */
export function LibraryBrowser({
  library,
  folders,
  views: initialViews,
}: {
  library: LibraryView;
  folders: FolderView[];
  views: SavedView[];
}) {
  const t = useTranslations("documents.browser");
  const locale = useLocale();
  const ids = useId();
  const [folder, setFolder] = useState<string | null>(null);
  const [type, setType] = useState<string>("");
  const [text, setText] = useState("");
  const [filters, setFilters] = useState<Record<string, string>>({});
  const [sort, setSort] = useState("modified");
  const [descending, setDescending] = useState(true);
  const [view, setView] = useState<string>("");
  const [views, setViews] = useState(initialViews);
  const [viewName, setViewName] = useState("");
  const [answer, setAnswer] = useState<Answer>("loading");
  const [attempt, setAttempt] = useState(0);
  const [messages, setMessages] = useState<string[]>([]);
  const [applied, setApplied] = useState("");
  const [now, setNow] = useState(0);

  const selectedType = library.types.find((x) => x.id === type) ?? null;
  const filterable = (selectedType?.fields ?? []).filter(
    (f) => f.kind === "choice" || f.kind === "boolean",
  );

  const query = useMemo(() => {
    const q = new URLSearchParams({
      limit: "50",
      sort,
      desc: String(descending),
    });
    if (view) q.set("view", view);
    if (folder) q.set("folder", folder);
    if (type) q.set("type", type);
    if (applied) q.set("q", applied);
    for (const [k, v] of Object.entries(filters)) if (v) q.set(`f.${k}`, v);
    return q.toString();
  }, [view, folder, type, applied, filters, sort, descending]);

  useEffect(() => {
    let live = true;
    fetch(
      `/api/v1/documents/libraries/${encodeURIComponent(library.id)}/items?${query}`,
    )
      .then(async (r) => {
        if (!r.ok) throw new Error(String(r.status));
        const page = (await r.json()) as ItemPage;
        if (live) {
          setNow(Date.now());
          setAnswer(page);
        }
      })
      .catch(() => live && setAnswer("failed"));
    return () => {
      live = false;
    };
  }, [library.id, query, attempt]);

  const reload = () => {
    setAnswer("loading");
    setAttempt((a) => a + 1);
  };

  async function saveView(e: React.FormEvent) {
    e.preventDefault();
    setMessages([]);
    const response = await fetch(
      `/api/v1/documents/libraries/${encodeURIComponent(library.id)}/views`,
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          names: { [locale]: viewName },
          query: {
            folderId: folder,
            typeId: type || null,
            text: applied || null,
            filters,
            sort,
            descending,
          },
        }),
      },
    );
    if (!response.ok) {
      setMessages(await problemMessages(response));
      return;
    }
    const saved = (await response.json()) as SavedView;
    setViews((v) => [...v, saved]);
    setView(saved.id);
    setViewName("");
  }

  const folderName = (id: string | null) =>
    id ? (folders.find((f) => f.id === id)?.name ?? "") : t("root");
  const children = folders.filter((f) => f.parentId === folder);
  /** The folders above the current one, top first, for the path; the current folder itself is not a link. */
  const path: FolderView[] = [];
  for (let at = folders.find((f) => f.id === folder)?.parentId ?? null; at; at = folders.find((f) => f.id === at)?.parentId ?? null) {
    const f = folders.find((x) => x.id === at);
    if (!f) break;
    path.unshift(f);
  }
  const narrowed = applied !== "" || type !== "" || view !== "" || Object.values(filters).some(Boolean);
  const clear = () => {
    setText("");
    setApplied("");
    setType("");
    setView("");
    setFilters({});
  };

  return (
    <div className="documents-browser">
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <section className="documents-toolbar" aria-label={t("filters")}>
        <form
          className="documents-search"
          role="search"
          onSubmit={(e) => {
            e.preventDefault();
            setApplied(text.trim());
          }}
        >
          <label htmlFor={`${ids}-q`} className="visually-hidden">
            {t("search")}
          </label>
          <span className="documents-search-box">
            <Icon name="search" size={16} />
            <input id={`${ids}-q`} type="search" placeholder={t("search")} value={text} onChange={(e) => setText(e.target.value)} />
          </span>
          <Button type="submit" variant="secondary">
            {t("apply")}
          </Button>
        </form>
        <label htmlFor={`${ids}-type`} className="visually-hidden">
          {t("type")}
        </label>
        <select
          id={`${ids}-type`}
          value={type}
          onChange={(e) => {
            setType(e.target.value);
            setFilters({});
          }}
        >
          <option value="">{t("anyType")}</option>
          {library.types.map((x) => (
            <option key={x.id} value={x.id}>
              {nameIn(x.names, locale)}
            </option>
          ))}
        </select>
        {filterable.map((f) => (
          <span key={f.key} className="documents-filter">
            <label htmlFor={`${ids}-f-${f.key}`} className="visually-hidden">
              {nameIn(f.names, locale)}
            </label>
            <select id={`${ids}-f-${f.key}`} value={filters[f.key] ?? ""} onChange={(e) => setFilters((x) => ({ ...x, [f.key]: e.target.value }))}>
              <option value="">{t("anyField", { field: nameIn(f.names, locale) })}</option>
              {f.kind === "boolean" ? (
                <>
                  <option value="true">{t("yes")}</option>
                  <option value="false">{t("no")}</option>
                </>
              ) : (
                (f.options ?? []).map((o) => (
                  <option key={o.value} value={o.value}>
                    {nameIn(o.names, locale)}
                  </option>
                ))
              )}
            </select>
          </span>
        ))}
        {views.length > 0 ? (
          <>
            <label htmlFor={`${ids}-view`} className="visually-hidden">
              {t("savedViews")}
            </label>
            <select id={`${ids}-view`} value={view} onChange={(e) => setView(e.target.value)}>
              <option value="">{t("noView")}</option>
              {views.map((v) => (
                <option key={v.id} value={v.id}>
                  {nameIn(v.names, locale)}
                </option>
              ))}
            </select>
          </>
        ) : null}
        <span className="documents-toolbar-end">
          <label htmlFor={`${ids}-sort`} className="visually-hidden">
            {t("sort")}
          </label>
          <select id={`${ids}-sort`} value={sort} onChange={(e) => setSort(e.target.value)}>
            <option value="modified">{t("sortModified")}</option>
            <option value="name">{t("sortName")}</option>
            {(selectedType?.fields ?? []).map((f) => (
              <option key={f.key} value={f.key}>
                {nameIn(f.names, locale)}
              </option>
            ))}
          </select>
          <button
            type="button"
            className="button button-secondary documents-direction"
            aria-pressed={descending}
            title={t("descending")}
            onClick={() => setDescending((d) => !d)}
          >
            <Icon name={descending ? "sort-desc" : "sort-asc"} size={16} />
            <span className="visually-hidden">{t("descending")}</span>
          </button>
        </span>
      </section>

      <nav aria-label={t("folders")} className="documents-folders">
        <div className="documents-where">
        <ol className="documents-path">
          {folder ? (
            <li>
              <button type="button" className="documents-crumb" onClick={() => setFolder(null)}>
                {t("root")}
              </button>
            </li>
          ) : null}
          {path.map((f) => (
            <li key={f.id}>
              <button type="button" className="documents-crumb" onClick={() => setFolder(f.id)}>
                <bdi>{f.name}</bdi>
              </button>
            </li>
          ))}
        </ol>
        <p className="documents-here">
          <span className="visually-hidden">{t("inFolder")} </span>
          <Icon name="folder" size={16} />
          <bdi data-testid="current-folder">{folderName(folder)}</bdi>
          {folder ? (
            <Button variant="quiet" onClick={() => setFolder(folders.find((f) => f.id === folder)?.parentId ?? null)}>
              {t("up")}
            </Button>
          ) : null}
        </p>
        </div>
        {children.length > 0 ? (
          <ul className="documents-folder-chips">
            {children.map((f) => (
              <li key={f.id}>
                <button type="button" className="documents-folder-chip" onClick={() => setFolder(f.id)}>
                  <Icon name="folder" size={16} />
                  <bdi>{f.name}</bdi>
                </button>
              </li>
            ))}
          </ul>
        ) : null}
      </nav>

      {library.rights.contribute ? (
        <UploadDialog library={library} folderId={folder} onUploaded={reload} />
      ) : null}

      <section
        aria-label={t("results")}
        aria-busy={answer === "loading" || undefined}
      >
        {answer === "loading" ? (
          <StateView kind="loading" minBlockSize="10rem" />
        ) : answer === "failed" ? (
          <StateView
            kind="error"
            title={t("failed")}
            message=""
            action={<Button onClick={reload}>{t("retry")}</Button>}
          />
        ) : answer.items.length === 0 ? (
          <StateView
            kind="empty"
            icon={narrowed ? "search" : "folder"}
            title={t("none")}
            message={narrowed ? t("noneHint") : ""}
            action={
              narrowed ? (
                <Button variant="secondary" onClick={clear}>
                  {t("clear")}
                </Button>
              ) : undefined
            }
          />
        ) : (
          <>
            <p className="documents-meta">
              {t("total", { count: answer.total })}
            </p>
            <div className="documents-table">
              <table>
                <thead>
                  <tr>
                    <th scope="col">{t("name")}</th>
                    <th scope="col">{t("version")}</th>
                    <th scope="col">{t("state")}</th>
                    <th scope="col">{t("updated")}</th>
                  </tr>
                </thead>
                <tbody>
                  {answer.items.map((d) => (
                    <tr key={d.id}>
                      <td>
                        <Link href={documentHref(d.id)} className="documents-name">
                          <Icon name="files" size={18} />
                          <bdi>{d.name}</bdi>
                        </Link>
                        {d.checkedOutBy ? (
                          <span className="ui-badge" data-tone="warning">
                            {t("checkedOut")}
                          </span>
                        ) : null}
                      </td>
                      <td>{d.versionLabel ?? ""}</td>
                      <td>{d.state ? t(`stateValue.${d.state}`) : ""}</td>
                      <td>
                        <time dateTime={d.updatedAt} title={dateTimeFull(d.updatedAt, locale)}>
                          {relativeTime(d.updatedAt, locale, now)}
                        </time>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </section>

      {library.rights.manage ? (
        <form
          className="documents-save-view"
          onSubmit={(e) => void saveView(e)}
        >
          <div className="field">
            <label htmlFor={`${ids}-view-name`}>{t("viewName")}</label>
            <input
              id={`${ids}-view-name`}
              value={viewName}
              onChange={(e) => setViewName(e.target.value)}
              maxLength={80}
            />
          </div>
          <Button type="submit" disabled={!viewName.trim()}>
            {t("saveView")}
          </Button>
        </form>
      ) : null}
    </div>
  );
}
