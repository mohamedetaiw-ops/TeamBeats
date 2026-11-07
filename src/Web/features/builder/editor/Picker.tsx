"use client";

import { useEffect, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import type { PickerItem } from "./model";

/**
 * The component picker for one region (spec 032 FR-018): what the API offers for it, with the reason when a component
 * cannot go there. A modal dialog, so focus stays inside until the editor chooses or closes it.
 */
export function Picker({
  pageId,
  regionId,
  regionName,
  onChoose,
  onClose,
}: {
  pageId: string;
  regionId: string;
  regionName: string;
  onChoose: (item: PickerItem) => void;
  onClose: () => void;
}) {
  const t = useTranslations("builder.picker");
  const dialog = useRef<HTMLDialogElement>(null);
  const [result, setResult] = useState<{ key: string; items: PickerItem[] | "failed" } | null>(null);
  const [query, setQuery] = useState("");
  const key = `${pageId}:${regionId}`;

  useEffect(() => {
    dialog.current?.showModal();
  }, []);

  useEffect(() => {
    let live = true;
    fetch(`/api/v1/experience/pages/${pageId}/components?region=${encodeURIComponent(regionId)}`)
      .then(async (r) => (r.ok ? ((await r.json()) as PickerItem[]) : ("failed" as const)))
      .catch(() => "failed" as const)
      .then((items) => {
        if (live) setResult({ key, items });
      });
    return () => {
      live = false;
    };
  }, [pageId, regionId, key]);

  const items = result?.key === key ? result.items : null;
  const needle = query.trim().toLocaleLowerCase();
  const shown =
    items && items !== "failed"
      ? items
          .filter((i) => !needle || i.name.toLocaleLowerCase().includes(needle) || i.description.toLocaleLowerCase().includes(needle))
          .sort((a, b) => Number(b.eligible) - Number(a.eligible) || a.name.localeCompare(b.name))
      : [];

  return (
    <dialog ref={dialog} className="builder-picker" aria-labelledby="builder-picker-title" onClose={onClose}>
      <div className="builder-heading">
        <h2 id="builder-picker-title">{t("title", { region: regionName })}</h2>
        <button type="button" className="button button-quiet" onClick={() => dialog.current?.close()}>
          {t("close")}
        </button>
      </div>
      <div className="field">
        <label htmlFor="builder-picker-search">{t("search")}</label>
        <input id="builder-picker-search" type="search" value={query} onChange={(e) => setQuery(e.target.value)} />
      </div>
      {items === null ? (
        <p role="status">{t("loading")}</p>
      ) : items === "failed" ? (
        <p role="alert" className="field-error">
          {t("failed")}
        </p>
      ) : shown.length === 0 ? (
        <p role="status">{t("empty")}</p>
      ) : (
        <ul className="builder-picker-list">
          {shown.map((item) => (
            <li key={item.componentRef} className="builder-picker-item" data-eligible={item.eligible || undefined}>
              <span className="builder-template-name">{item.name}</span>
              <span className="builder-template-description">{item.description}</span>
              {item.eligible ? (
                <button
                  type="button"
                  className="button button-secondary"
                  aria-label={t("addLabel", { name: item.name })}
                  onClick={() => {
                    onChoose(item);
                    dialog.current?.close();
                  }}
                >
                  {t("add")}
                </button>
              ) : (
                <span className="builder-reason">{item.reason}</span>
              )}
            </li>
          ))}
        </ul>
      )}
    </dialog>
  );
}
