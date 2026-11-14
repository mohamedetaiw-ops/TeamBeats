"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import type { BodyDocument } from "@/lib/content/types";
import { problemMessages } from "@/lib/notifications/types";
import { policyHref, type Names, type VersionView } from "@/lib/policies/types";
import { Button } from "@/components/ui/Button";
import { BlockEditor } from "@/features/content/editor/BlockEditor";
import "./policies.css";

type Clause = { uid: number; key: string | null; level: number; titles: Names; bodies: Record<string, BodyDocument> };

const empty: BodyDocument = { blocks: [] };

// React keys for clauses while editing; the clause key itself comes from the API.
let sequence = 0;
const make = (c: Omit<Clause, "uid">): Clause => ({ ...c, uid: ++sequence });

/**
 * The policy editor (spec 025 US1, FR-003): the title and numbered clauses in each language of the tenant, with levels
 * 1 to 3 that give the numbering, the material or minor flag and a change note. Clauses keep their key when moved, so
 * their anchors and comments follow them. Saving and submitting go to the API, which checks completeness and refuses
 * changes while the version is in review.
 */
export function DraftEditor({ policyId, initial, locales }: { policyId: string; initial: VersionView | null; locales: string[] }) {
  const t = useTranslations("policies.editor");
  const ids = useId();
  const router = useRouter();
  const [tab, setTab] = useState(locales[0] ?? "en");
  const [titles, setTitles] = useState<Names>(initial?.titles ?? {});
  const [clauses, setClauses] = useState<Clause[]>(() =>
    initial && initial.clauses.length > 0
      ? initial.clauses.map((c) => make({ key: c.key, level: c.level, titles: c.titles, bodies: c.bodies }))
      : [make({ key: null, level: 1, titles: {}, bodies: {} })],
  );
  const [material, setMaterial] = useState(initial?.material ?? true);
  const [changeNote, setChangeNote] = useState(initial?.changeNote ?? "");
  const [effectiveOn, setEffectiveOn] = useState("");
  const [busy, setBusy] = useState<"save" | "submit" | "withdraw" | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);
  const locked = initial?.state === "in_review";

  const update = (uid: number, patch: Partial<Clause>) => setClauses((list) => list.map((c) => (c.uid === uid ? { ...c, ...patch } : c)));
  const move = (index: number, by: number) =>
    setClauses((list) => {
      const target = index + by;
      if (target < 0 || target >= list.length) return list;
      const copy = [...list];
      [copy[index], copy[target]] = [copy[target]!, copy[index]!];
      return copy;
    });

  async function call(path: string, method: "PUT" | "POST", body: unknown): Promise<Response | null> {
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(policyId)}/${path}`, {
        method,
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return null;
      }
      return response;
    } catch {
      setMessages([t("failed")]);
      return null;
    }
  }

  async function save(): Promise<boolean> {
    const response = await call("draft", "PUT", {
      titles,
      clauses: clauses.map((c) => ({ key: c.key, level: c.level, titles: c.titles, bodies: c.bodies })),
      material,
      changeNote: changeNote.trim() || null,
    });
    if (!response) return false;
    const version = (await response.json()) as VersionView;
    // New clauses now have keys; keep them so the next save updates rather than replaces them.
    setClauses((list) => list.map((c, i) => ({ ...c, key: version.clauses[i]?.key ?? c.key })));
    return true;
  }

  async function run(action: "save" | "submit" | "withdraw") {
    setBusy(action);
    setMessages([]);
    setSaved(false);
    try {
      if (action === "withdraw") {
        if (await call("withdraw", "POST", {})) router.refresh();
        return;
      }
      if (!(await save())) return;
      if (action === "save") {
        setSaved(true);
        return;
      }
      if (await call("submit", "POST", { effectiveOn: effectiveOn || null })) {
        router.push(policyHref(policyId));
      }
    } finally {
      setBusy(null);
    }
  }

  return (
    <form className="policies-page" onSubmit={(e) => e.preventDefault()} noValidate>
      {locked ? (
        <p className="policies-banner" role="note">
          {t("inReview")}
        </p>
      ) : null}
      <div role="tablist" aria-label={t("languages")} className="ui-tablist">
        {locales.map((l) => (
          <button
            key={l}
            type="button"
            role="tab"
            id={`${ids}-tab-${l}`}
            aria-selected={tab === l}
            aria-controls={`${ids}-panel`}
            tabIndex={tab === l ? 0 : -1}
            className="ui-tab"
            onClick={() => setTab(l)}
            onKeyDown={(e) => {
              if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
              const forward = (e.key === "ArrowRight") !== (document.dir === "rtl");
              const to = locales[(locales.indexOf(l) + (forward ? 1 : locales.length - 1)) % locales.length]!;
              setTab(to);
              document.getElementById(`${ids}-tab-${to}`)?.focus();
            }}
          >
            {t(`language.${l}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel" id={`${ids}-panel`} aria-labelledby={`${ids}-tab-${tab}`} className="policies-page" lang={tab} dir={tab === "ar" ? "rtl" : "ltr"}>
        <div className="field">
          <label htmlFor={`${ids}-title`}>{t("title")}</label>
          <input
            id={`${ids}-title`}
            value={titles[tab] ?? ""}
            onChange={(e) => setTitles((v) => ({ ...v, [tab]: e.target.value }))}
            maxLength={300}
            disabled={locked}
          />
        </div>
        <ol className="policies-list" aria-label={t("clauses")}>
          {clauses.map((c, i) => (
            <li key={c.uid} className="policies-clause-editor" data-level={c.level}>
              <fieldset disabled={locked}>
                <legend className="visually-hidden">{t("clause", { position: i + 1 })}</legend>
                <div className="field">
                  <label htmlFor={`${ids}-${c.uid}-level`}>{t("level")}</label>
                  <select id={`${ids}-${c.uid}-level`} value={c.level} onChange={(e) => update(c.uid, { level: Number(e.target.value) })}>
                    {[1, 2, 3].map((n) => (
                      <option key={n} value={n}>
                        {t(`levels.${n}`)}
                      </option>
                    ))}
                  </select>
                </div>
                <div className="field">
                  <label htmlFor={`${ids}-${c.uid}-title`}>{t("clauseTitle", { position: i + 1 })}</label>
                  <input
                    id={`${ids}-${c.uid}-title`}
                    value={c.titles[tab] ?? ""}
                    onChange={(e) => update(c.uid, { titles: { ...c.titles, [tab]: e.target.value } })}
                    maxLength={300}
                  />
                </div>
                <BlockEditor
                  key={`${tab}-${c.uid}`}
                  label={t("clauseText", { position: i + 1 })}
                  locale={tab}
                  value={c.bodies[tab] ?? empty}
                  onChange={(body) => update(c.uid, { bodies: { ...c.bodies, [tab]: body } })}
                />
                <div className="button-row">
                  <Button type="button" variant="quiet" onClick={() => move(i, -1)} disabled={i === 0}>
                    {t("moveUp", { position: i + 1 })}
                  </Button>
                  <Button type="button" variant="quiet" onClick={() => move(i, 1)} disabled={i === clauses.length - 1}>
                    {t("moveDown", { position: i + 1 })}
                  </Button>
                  <Button
                    type="button"
                    variant="quiet"
                    onClick={() => setClauses((list) => list.filter((x) => x.uid !== c.uid))}
                    disabled={clauses.length === 1}
                  >
                    {t("remove", { position: i + 1 })}
                  </Button>
                </div>
              </fieldset>
            </li>
          ))}
        </ol>
        {!locked ? (
          <div className="button-row">
            <Button type="button" onClick={() => setClauses((list) => [...list, make({ key: null, level: 1, titles: {}, bodies: {} })])}>
              {t("add")}
            </Button>
          </div>
        ) : null}
      </div>
      <fieldset className="policies-card" disabled={locked}>
        <legend>{t("change")}</legend>
        <label className="policies-check">
          <input type="radio" name={`${ids}-material`} checked={material} onChange={() => setMaterial(true)} />
          {t("material")}
        </label>
        <label className="policies-check">
          <input type="radio" name={`${ids}-material`} checked={!material} onChange={() => setMaterial(false)} />
          {t("minor")}
        </label>
        <div className="field">
          <label htmlFor={`${ids}-note`}>{t("changeNote")}</label>
          <textarea id={`${ids}-note`} value={changeNote} onChange={(e) => setChangeNote(e.target.value)} maxLength={1000} rows={2} dir="auto" />
        </div>
        <div className="field">
          <label htmlFor={`${ids}-effective`}>{t("effectiveOn")}</label>
          <input id={`${ids}-effective`} type="date" value={effectiveOn} onChange={(e) => setEffectiveOn(e.target.value)} />
        </div>
      </fieldset>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {saved ? (
        <p className="notice" role="status">
          {t("saved")}
        </p>
      ) : null}
      <div className="button-row">
        {locked ? (
          <Button type="button" busy={busy === "withdraw"} busyLabel={t("busy")} onClick={() => void run("withdraw")}>
            {t("withdraw")}
          </Button>
        ) : (
          <>
            <Button type="button" variant="primary" busy={busy === "submit"} busyLabel={t("busy")} onClick={() => void run("submit")}>
              {t("submit")}
            </Button>
            <Button type="button" busy={busy === "save"} busyLabel={t("busy")} onClick={() => void run("save")}>
              {t("save")}
            </Button>
          </>
        )}
      </div>
    </form>
  );
}
