"use client";

import Link from "next/link";
import { useEffect, useId, useMemo, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
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
  const format = useFormatter();
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
        if (live) setAnswer(page);
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
        <div className="field">
          <label htmlFor={`${ids}-view`}>{t("savedViews")}</label>
          <select
            id={`${ids}-view`}
            value={view}
            onChange={(e) => setView(e.target.value)}
          >
            <option value="">{t("noView")}</option>
            {views.map((v) => (
              <option key={v.id} value={v.id}>
                {nameIn(v.names, locale)}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor={`${ids}-type`}>{t("type")}</label>
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
        </div>
        {filterable.map((f) => (
          <div className="field" key={f.key}>
            <label htmlFor={`${ids}-f-${f.key}`}>
              {nameIn(f.names, locale)}
            </label>
            <select
              id={`${ids}-f-${f.key}`}
              value={filters[f.key] ?? ""}
              onChange={(e) =>
                setFilters((x) => ({ ...x, [f.key]: e.target.value }))
              }
            >
              <option value="">{t("anyValue")}</option>
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
          </div>
        ))}
        <form
          className="documents-search"
          role="search"
          onSubmit={(e) => {
            e.preventDefault();
            setApplied(text.trim());
          }}
        >
          <div className="field">
            <label htmlFor={`${ids}-q`}>{t("search")}</label>
            <input
              id={`${ids}-q`}
              type="search"
              value={text}
              onChange={(e) => setText(e.target.value)}
            />
          </div>
          <Button type="submit">{t("apply")}</Button>
        </form>
        <div className="field">
          <label htmlFor={`${ids}-sort`}>{t("sort")}</label>
          <select
            id={`${ids}-sort`}
            value={sort}
            onChange={(e) => setSort(e.target.value)}
          >
            <option value="modified">{t("sortModified")}</option>
            <option value="name">{t("sortName")}</option>
            {(selectedType?.fields ?? []).map((f) => (
              <option key={f.key} value={f.key}>
                {nameIn(f.names, locale)}
              </option>
            ))}
          </select>
        </div>
        <label className="documents-check">
          <input
            type="checkbox"
            checked={descending}
            onChange={(e) => setDescending(e.target.checked)}
          />
          {t("descending")}
        </label>
      </section>

      <nav aria-label={t("folders")} className="documents-folders">
        <p>
          <span>{t("inFolder")} </span>
          <bdi data-testid="current-folder">{folderName(folder)}</bdi>
          {folder ? (
            <Button
              variant="quiet"
              onClick={() =>
                setFolder(
                  folders.find((f) => f.id === folder)?.parentId ?? null,
                )
              }
            >
              {t("up")}
            </Button>
          ) : null}
        </p>
        {children.length > 0 ? (
          <ul>
            {children.map((f) => (
              <li key={f.id}>
                <Button variant="quiet" onClick={() => setFolder(f.id)}>
                  <bdi>{f.name}</bdi>
                </Button>
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
          <StateView kind="empty" title={t("none")} message="" />
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
                        <Link href={documentHref(d.id)}>
                          <bdi>{d.name}</bdi>
                        </Link>
                        {d.checkedOutBy ? (
                          <span className="documents-badge">
                            {t("checkedOut")}
                          </span>
                        ) : null}
                      </td>
                      <td>{d.versionLabel ?? ""}</td>
                      <td>{d.state ? t(`stateValue.${d.state}`) : ""}</td>
                      <td>
                        <time dateTime={d.updatedAt}>
                          {format.dateTime(new Date(d.updatedAt), {
                            dateStyle: "medium",
                          })}
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
