"use client";

import { useCallback, useEffect, useRef, useState, type KeyboardEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { CatalogueTemplate, Finding, PageDefinition, PageView } from "@/lib/experience/builder-types";
import { Button } from "@/components/ui/Button";
import * as model from "./model";
import { Picker } from "./Picker";
import { PropertyForm } from "./PropertyForm";

type SaveState = "saved" | "unsaved" | "saving" | "offline" | "blocked" | "conflict" | "failed";
type Backup = { etag: string; draft: PageDefinition };

const SAVE_DELAY_MS = 1200;

/** Unsaved edits are kept in this browser per tenant and page, never shared between tenants (ADR-0014). */
const backupKey = (tenantId: string, pageId: string) => `tb.builder.${tenantId}.${pageId}`;

function readBackup(key: string): Backup | null {
  try {
    const raw = window.localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as Backup) : null;
  } catch {
    return null;
  }
}

function writeBackup(key: string, backup: Backup | null) {
  try {
    if (backup) window.localStorage.setItem(key, JSON.stringify(backup));
    else window.localStorage.removeItem(key);
  } catch {
    // Storage can be full or blocked; the draft stays in memory.
  }
}

const isTyping = (target: EventTarget | null) =>
  target instanceof HTMLElement && (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName));

/**
 * The page editor (spec 032 US1, T018): regions with their placements, the picker, schema-driven property forms, an
 * action menu and shortcuts for every placement operation, undo and redo, autosave with its states, and publish. Every
 * rule is the API's: the editor shows what the API answers and never decides what is valid.
 */
export function PageEditor({
  page,
  template,
  tenantId,
  locales,
  catalogueItems,
}: {
  page: PageView;
  template: CatalogueTemplate | undefined;
  tenantId: string;
  locales: string[];
  catalogueItems: model.PickerItem[];
}) {
  const t = useTranslations("builder.editor");
  const tr = useTranslations("builder");
  const uiLocale = useLocale();
  const languages = new Intl.DisplayNames([uiLocale], { type: "language" });
  const language = (l: string) => languages.of(l) ?? l;
  const key = backupKey(tenantId, page.id);
  const items = new Map(catalogueItems.map((i) => [i.componentRef, i]));
  const regionName = (id: string) => tr(`regions.${id}`);

  // A backup from an earlier visit wins over the stored draft; it saves against the version it started from.
  const [restored] = useState(() => {
    const backup = readBackup(key);
    return backup && JSON.stringify(backup.draft) !== JSON.stringify(page.draft) ? backup : null;
  });
  const [history, setHistory] = useState<model.History>({ past: [], present: restored?.draft ?? page.draft, future: [] });
  const [saved, setSaved] = useState<PageDefinition>(page.draft);
  const [state, setState] = useState<SaveState>(restored ? "unsaved" : "saved");
  const [findings, setFindings] = useState<Finding[]>(page.findings);
  const [conflict, setConflict] = useState<{ etag: string; current: PageDefinition } | null>(null);
  const [picker, setPicker] = useState<string | null>(null);
  const [menu, setMenu] = useState<string | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState(restored ? t("restored") : "");
  const [reason, setReason] = useState("");
  const [publishing, setPublishing] = useState(false);
  const [savedCount, setSavedCount] = useState(0);
  const [device, setDevice] = useState<"phone" | "tablet" | "desktop">("desktop");
  const [previewLocale, setPreviewLocale] = useState(locales.includes(uiLocale) ? uiLocale : (locales[0] ?? uiLocale));
  const [published, setPublished] = useState<number | null>(page.currentRelease);
  const etag = useRef(restored?.etag ?? page.eTag);
  const focusAfter = useRef<string | null>(null);
  const cards = useRef(new Map<string, HTMLElement>());
  const picked = useRef(false);

  const draft = history.present;
  const dirty = draft !== saved;

  const edit = useCallback((next: PageDefinition, focus?: string | null, say?: string) => {
    focusAfter.current = focus ?? null;
    setHistory((h) => model.record(h, next));
    if (say) setAnnouncement(say);
  }, []);

  const save = useCallback(
    async (definition: PageDefinition, ifMatch: string) => {
      setState("saving");
      let response: Response;
      try {
        response = await fetch(`/api/v1/experience/pages/${page.id}/draft`, {
          method: "PUT",
          headers: { "content-type": "application/json", "if-match": ifMatch },
          body: JSON.stringify({ definition }),
        });
      } catch {
        writeBackup(key, { etag: ifMatch, draft: definition });
        setState("offline");
        return;
      }
      if (response.ok) {
        const view = (await response.json()) as PageView;
        etag.current = response.headers.get("etag") ?? view.eTag;
        writeBackup(key, null);
        setSaved(definition);
        setSavedCount((n) => n + 1);
        setFindings(view.findings);
        setConflict(null);
        setState("saved");
        return;
      }
      writeBackup(key, { etag: ifMatch, draft: definition });
      const problem = (await response.json().catch(() => ({}))) as { findings?: Finding[]; etag?: string; current?: PageDefinition };
      if (response.status === 409 && problem.etag && problem.current) {
        setConflict({ etag: problem.etag, current: problem.current });
        setState("conflict");
      } else if (response.status === 422 && problem.findings) {
        setFindings(problem.findings);
        setState("blocked");
      } else {
        setState("failed");
      }
    },
    [key, page.id],
  );

  // Autosave a moment after the last edit, unless a conflict waits for the editor's choice.
  useEffect(() => {
    if (!dirty || conflict) return;
    const timer = setTimeout(() => void save(draft, etag.current), SAVE_DELAY_MS);
    return () => clearTimeout(timer);
  }, [draft, dirty, conflict, save]);

  // Back online: save what was kept.
  useEffect(() => {
    const online = () => {
      if (dirty && !conflict) void save(draft, etag.current);
    };
    window.addEventListener("online", online);
    return () => window.removeEventListener("online", online);
  }, [draft, dirty, conflict, save]);

  // Focus stays on the placement an operation acted on (FR-040).
  useEffect(() => {
    const id = focusAfter.current;
    focusAfter.current = null;
    if (id) cards.current.get(id)?.focus();
  }, [draft]);

  const position = (d: PageDefinition, id: string) => {
    const region = d.regions.find((r) => r.placements.some((p) => p.id === id))!;
    return { region: regionName(region.id), number: String(region.placements.findIndex((p) => p.id === id) + 1), count: String(region.placements.length) };
  };
  const nameOf = (ref: string) => items.get(ref)?.name ?? ref;

  const ops = {
    move(id: string, ref: string, step: -1 | 1) {
      const next = model.move(draft, id, step);
      if (next === draft) {
        setAnnouncement(t(step < 0 ? "atStart" : "atEnd", { name: nameOf(ref) }));
        return;
      }
      edit(next, id, t("moved", { name: nameOf(ref), ...position(next, id) }));
    },
    moveTo(id: string, ref: string, region: string) {
      const next = model.moveTo(draft, id, region);
      edit(next, id, t("moved", { name: nameOf(ref), ...position(next, id) }));
    },
    duplicate(id: string, ref: string) {
      const { draft: next, id: copy } = model.duplicate(draft, id);
      if (copy) edit(next, copy, t("duplicated", { name: nameOf(ref) }));
    },
    remove(id: string, ref: string) {
      const region = model.regionOf(draft, id);
      edit(model.remove(draft, id), null, t("removed", { name: nameOf(ref) }));
      if (region) focusAfter.current = `add:${region}`;
    },
  };

  const undo = () => {
    if (history.past.length === 0) return;
    setHistory(model.undo);
    setAnnouncement(t("undone"));
  };
  const redo = () => {
    if (history.future.length === 0) return;
    setHistory(model.redo);
    setAnnouncement(t("redone"));
  };

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const mod = event.ctrlKey || event.metaKey;
    if (mod && !isTyping(event.target) && event.key.toLowerCase() === "z") {
      event.preventDefault();
      if (event.shiftKey) redo();
      else undo();
    } else if (mod && !isTyping(event.target) && event.key.toLowerCase() === "y") {
      event.preventDefault();
      redo();
    }
  };

  const onCardKey = (event: KeyboardEvent<HTMLElement>, id: string, ref: string) => {
    if (event.target !== event.currentTarget) return;
    if (event.altKey && (event.key === "ArrowUp" || event.key === "ArrowDown")) {
      event.preventDefault();
      ops.move(id, ref, event.key === "ArrowUp" ? -1 : 1);
    } else if (event.altKey && event.key.toLowerCase() === "d") {
      event.preventDefault();
      ops.duplicate(id, ref);
    } else if (event.key === "Delete") {
      event.preventDefault();
      ops.remove(id, ref);
    }
  };

  async function publish() {
    setPublishing(true);
    try {
      if (dirty) await save(draft, etag.current);
      const response = await fetch(`/api/v1/experience/pages/${page.id}/publish`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ reason: reason.trim() || null }),
      });
      if (response.ok) {
        const release = (await response.json()) as { release: number };
        setPublished(release.release);
        setReason("");
        setAnnouncement(t("publishedNow", { release: String(release.release) }));
        return;
      }
      const problem = (await response.json().catch(() => ({}))) as { findings?: Finding[] };
      if (problem.findings) setFindings(problem.findings);
      setAnnouncement(t("publishBlocked"));
    } catch {
      setAnnouncement(t("publishFailed"));
    } finally {
      setPublishing(false);
    }
  }

  const status = state === "saved" && dirty ? "unsaved" : state;
  const pageFindings = findings.filter((f) => !f.placement);
  const warnings = findings.filter((f) => f.severity !== "blocking").length;
  const missing = (l: string) => findings.filter((f) => f.code === "validation.page.translation_missing" && f.locale === l).length;
  const findingText = (f: Finding) => (f.locale ? t("missingIn", { message: f.message ?? f.code, language: language(f.locale) }) : (f.message ?? f.code));
  const blocking = findings.filter((f) => f.severity === "blocking").length;

  return (
    <div className="builder-editor" onKeyDown={onKeyDown}>
      <div className="builder-toolbar" role="toolbar" aria-label={t("toolbar")}>
        <Button variant="secondary" onClick={undo} disabled={history.past.length === 0} aria-keyshortcuts="Control+Z">
          {t("undo")}
        </Button>
        <Button variant="secondary" onClick={redo} disabled={history.future.length === 0} aria-keyshortcuts="Control+Shift+Z">
          {t("redo")}
        </Button>
        <p className="builder-save" data-state={status} data-testid="save-state">
          {t(`save.${status}`)}
        </p>
        {status === "offline" || status === "failed" ? (
          <Button variant="secondary" onClick={() => void save(draft, etag.current)}>
            {t("retry")}
          </Button>
        ) : null}
      </div>

      <p className="builder-template-description">{t("shortcuts")}</p>

      <div className="visually-hidden" role="status" aria-live="polite" data-testid="announcer">
        {announcement}
      </div>

      {conflict ? (
        <div className="builder-conflict" role="alert">
          <p>{t("conflict")}</p>
          <div className="builder-actions">
            <Button
              variant="primary"
              onClick={() => {
                etag.current = conflict.etag;
                setConflict(null);
                void save(draft, conflict.etag);
              }}
            >
              {t("keepMine")}
            </Button>
            <Button
              variant="secondary"
              onClick={() => {
                etag.current = conflict.etag;
                writeBackup(key, null);
                setSaved(conflict.current);
                setHistory((h) => model.record(h, conflict.current));
                setConflict(null);
                setState("saved");
              }}
            >
              {t("takeTheirs")}
            </Button>
          </div>
        </div>
      ) : null}

      {pageFindings.length > 0 ? (
        <ul className="builder-findings" aria-label={t("pageFindings")}>
          {pageFindings.map((f, i) => (
            <li key={i} data-severity={f.severity}>
              {f.severity === "blocking" ? t("blocking") : t("warning")}{" "}
              {f.region ? t("inRegion", { region: regionName(f.region), message: f.message ?? f.code }) : (f.message ?? f.code)}
            </li>
          ))}
        </ul>
      ) : null}

      {draft.regions.map((region) => {
        const policy = template?.regions.find((r) => r.id === region.id)?.policy;
        const headingId = `builder-region-${region.id}`;
        return (
          <section key={region.id} className="builder-region" aria-labelledby={headingId}>
            <div className="builder-heading">
              <h2 id={headingId}>{regionName(region.id)}</h2>
              {policy ? <span className="builder-template-description">{t("limits", { min: String(policy.minPlacements), max: String(policy.maxPlacements) })}</span> : null}
            </div>
            {region.placements.length === 0 ? <p className="builder-template-description">{t("emptyRegion")}</p> : null}
            <ol className="builder-placements">
              {region.placements.map((p) => {
                const item = items.get(p.componentRef);
                const own = findings.filter((f) => f.placement === p.id);
                const problems = Object.fromEntries(own.filter((f) => f.property).map((f) => [f.property!, findingText(f)]));
                const name = nameOf(p.componentRef);
                const menuId = `builder-menu-${p.id}`;
                return (
                  <li key={p.id}>
                    <article
                      ref={(el) => {
                        if (el) cards.current.set(p.id, el);
                        else cards.current.delete(p.id);
                      }}
                      className="builder-placement"
                      tabIndex={0}
                      aria-label={t("placementLabel", { name, ...position(draft, p.id) })}
                      aria-keyshortcuts="Alt+ArrowUp Alt+ArrowDown Alt+D Delete"
                      data-testid={`placement-${p.id}`}
                      onKeyDown={(e) => onCardKey(e, p.id, p.componentRef)}
                    >
                      <div className="builder-heading">
                        <h3 className="builder-template-name">{name}</h3>
                        <div className="builder-actions">
                          <button
                            type="button"
                            className="button button-secondary"
                            aria-expanded={editing === p.id}
                            onClick={() => setEditing(editing === p.id ? null : p.id)}
                          >
                            {t("settings")}
                          </button>
                          <button
                            type="button"
                            className="button button-quiet"
                            aria-expanded={menu === p.id}
                            aria-controls={menuId}
                            aria-label={t("actionsFor", { name })}
                            onClick={() => setMenu(menu === p.id ? null : p.id)}
                          >
                            {t("actions")}
                          </button>
                        </div>
                      </div>
                      {menu === p.id ? (
                        <ul id={menuId} className="builder-menu">
                          <li>
                            <button type="button" className="button button-quiet" onClick={() => ops.move(p.id, p.componentRef, -1)}>
                              {t("moveUp")}
                            </button>
                          </li>
                          <li>
                            <button type="button" className="button button-quiet" onClick={() => ops.move(p.id, p.componentRef, 1)}>
                              {t("moveDown")}
                            </button>
                          </li>
                          {draft.regions
                            .filter((r) => r.id !== region.id)
                            .map((r) => (
                              <li key={r.id}>
                                <button
                                  type="button"
                                  className="button button-quiet"
                                  onClick={() => {
                                    setMenu(null);
                                    ops.moveTo(p.id, p.componentRef, r.id);
                                  }}
                                >
                                  {t("moveTo", { region: regionName(r.id) })}
                                </button>
                              </li>
                            ))}
                          <li>
                            <button
                              type="button"
                              className="button button-quiet"
                              onClick={() => {
                                setMenu(null);
                                ops.duplicate(p.id, p.componentRef);
                              }}
                            >
                              {t("duplicate")}
                            </button>
                          </li>
                          <li>
                            <button
                              type="button"
                              className="button button-quiet"
                              onClick={() => {
                                setMenu(null);
                                ops.remove(p.id, p.componentRef);
                              }}
                            >
                              {t("remove")}
                            </button>
                          </li>
                        </ul>
                      ) : null}
                      {own.filter((f) => !f.property).length > 0 ? (
                        <ul className="builder-findings">
                          {own
                            .filter((f) => !f.property)
                            .map((f, i) => (
                              <li key={i} data-severity={f.severity}>
                                {f.severity === "blocking" ? t("blocking") : t("warning")} {findingText(f)}
                              </li>
                            ))}
                        </ul>
                      ) : null}
                      {editing === p.id ? (
                        <PropertyForm
                          schema={item?.properties ?? {}}
                          labels={item?.labels ?? {}}
                          values={(p.props ?? {}) as Record<string, unknown>}
                          locales={locales}
                          problems={problems}
                          onChange={(props) => edit(model.setProps(draft, p.id, props))}
                        />
                      ) : null}
                    </article>
                  </li>
                );
              })}
            </ol>
            <button
              type="button"
              className="button button-secondary"
              ref={(el) => {
                if (el) cards.current.set(`add:${region.id}`, el);
                else cards.current.delete(`add:${region.id}`);
              }}
              onClick={() => setPicker(region.id)}
            >
              {t("add", { region: regionName(region.id) })}
            </button>
          </section>
        );
      })}

      <section className="builder-region builder-panel" aria-labelledby="builder-checks-title" data-testid="checks">
        <h2 id="builder-checks-title">{t("checks.title")}</h2>
        <p>{blocking > 0 ? t("checks.blocking", { count: blocking }) : t("checks.noBlocking")}</p>
        {warnings > 0 ? <p>{t("checks.warnings", { count: warnings })}</p> : null}
        <h3>{t("checks.translations")}</h3>
        <ul className="builder-findings">
          {locales.map((l) => (
            <li key={l} data-testid={`translation-${l}`}>
              {missing(l) === 0 ? t("checks.complete", { language: language(l) }) : t("checks.missing", { language: language(l), count: missing(l) })}
            </li>
          ))}
        </ul>
      </section>

      <section className="builder-region builder-panel" aria-labelledby="builder-preview-title">
        <h2 id="builder-preview-title">{t("preview.title")}</h2>
        <p className="builder-template-description">{t("preview.saved")}</p>
        <div className="builder-actions">
          <div className="builder-switch" role="group" aria-label={t("preview.device")}>
            {(["phone", "tablet", "desktop"] as const).map((d) => (
              <button key={d} type="button" className="button button-quiet" aria-pressed={device === d} onClick={() => setDevice(d)}>
                {t(`preview.devices.${d}`)}
              </button>
            ))}
          </div>
          <div className="field">
            <label htmlFor="builder-preview-locale">{t("preview.language")}</label>
            <select id="builder-preview-locale" value={previewLocale} onChange={(e) => setPreviewLocale(e.target.value)}>
              {locales.map((l) => (
                <option key={l} value={l}>
                  {language(l)}
                </option>
              ))}
            </select>
          </div>
        </div>
        <div className="builder-preview-frame" data-device={device}>
          <iframe
            key={`${previewLocale}-${savedCount}`}
            title={t("preview.frame", { device: t(`preview.devices.${device}`), language: language(previewLocale) })}
            src={`/preview/pages/${page.id}?locale=${encodeURIComponent(previewLocale)}&v=${savedCount}`}
            data-testid="preview-frame"
          />
        </div>
      </section>

      <section className="builder-region builder-publish" aria-labelledby="builder-publish-title">
        <h2 id="builder-publish-title">{t("publishTitle")}</h2>
        <p>{published === null ? tr("pages.draftOnly") : tr("pages.published", { release: String(published) })}</p>
        <div className="field">
          <label htmlFor="builder-reason">{t("reason")}</label>
          <input id="builder-reason" type="text" maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />
        </div>
        <Button variant="primary" busy={publishing} busyLabel={t("publishing")} disabled={blocking > 0 || !!conflict} onClick={() => void publish()}>
          {t("publish")}
        </Button>
        {blocking > 0 ? <p className="builder-template-description">{t("fixFirst")}</p> : null}
      </section>

      {picker ? (
        <Picker
          pageId={page.id}
          regionId={picker}
          regionName={regionName(picker)}
          onClose={() => {
            const region = picker;
            setPicker(null);
            if (!picked.current) cards.current.get(`add:${region}`)?.focus();
            picked.current = false;
          }}
          onChoose={(chosen) => {
            picked.current = true;
            const id = model.newPlacementId(draft, chosen.componentRef);
            const placement = { id, componentRef: chosen.componentRef, props: model.defaults(chosen.properties) };
            edit(model.add(draft, picker, placement), id, t("added", { name: chosen.name, region: regionName(picker) }));
            if (Object.keys(chosen.properties ?? {}).length > 0) setEditing(id);
          }}
        />
      ) : null}
    </div>
  );
}
