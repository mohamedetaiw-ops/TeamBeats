"use client";

import Link from "next/link";
import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import {
  classifications,
  membershipTypes,
  readHref,
  type BodyDocument,
  type ContentAudience,
  type ItemView,
  type SiteView,
} from "@/lib/content/types";
import { PersonPicker } from "@/features/services/PersonPicker";
import { BlockEditor } from "./editor/BlockEditor";
import { BodyView } from "./BodyView";
import "./content.css";

type Draft = { title: string; summary: string; body: BodyDocument };
type Result = { ok: boolean; messages: string[] } | null;

/** A UTC instant as the `datetime-local` value it is in the site's time zone. */
export function toLocalInput(iso: string | null, timeZone: string): string {
  if (!iso) return "";
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hourCycle: "h23",
  }).formatToParts(new Date(iso));
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? "";
  return `${get("year")}-${get("month")}-${get("day")}T${get("hour")}:${get("minute")}`;
}

const dateOnly = (iso: string | null, timeZone: string) => toLocalInput(iso, timeZone).slice(0, 10);

function Notice({ result }: { result: Result }) {
  if (!result) return null;
  return (
    <div className={result.ok ? "notice" : "notice notice-error"} role={result.ok ? "status" : "alert"}>
      {result.messages.map((m) => (
        <p key={m}>{m}</p>
      ))}
    </div>
  );
}

/**
 * The item editor (spec 009 T023): each language's title, summary and body in the block editor; owner, review date,
 * classification and placement; who it targets; critical options for announcements; publish now or at a time, with an
 * expiry, or send for approval; and a preview on desktop or phone width. The API checks every rule, including who may
 * publish, so the page only shows what it is told.
 */
export function ItemEditor({ initial, site, units, locales }: { initial: ItemView; site: SiteView; units: OrgUnit[]; locales: string[] }) {
  const t = useTranslations("content.items");
  const viewer = useLocale();
  const router = useRouter();
  const ids = useId();
  const [item, setItem] = useState(initial);
  const order = [initial.primaryLocale, ...locales.filter((l) => l !== initial.primaryLocale)];
  const [drafts, setDrafts] = useState<Record<string, Draft>>(() =>
    Object.fromEntries(
      order.map((l) => {
        const v = initial.variants[l];
        return [l, { title: v?.title ?? "", summary: v?.summary ?? "", body: v?.body ?? { blocks: [] } }];
      }),
    ),
  );
  const [tab, setTab] = useState(order[0]!);
  const [owner, setOwner] = useState<string | null>(initial.owner?.id ?? null);
  const [backup, setBackup] = useState<string | null>(initial.backupOwner?.id ?? null);
  const [reviewOn, setReviewOn] = useState(dateOnly(initial.reviewDueAt, site.timeZone));
  const [classification, setClassification] = useState(initial.classification);
  const [pinned, setPinned] = useState(initial.pinned);
  const [featured, setFeatured] = useState(initial.featured);
  const clause = initial.audience.anyOf?.[0];
  const [everyone, setEveryone] = useState(!initial.audience.anyOf?.length);
  const [audienceUnits, setAudienceUnits] = useState<string[]>(clause?.orgUnits?.map((u) => u.id) ?? []);
  const [subtree, setSubtree] = useState(clause?.orgUnits?.every((u) => u.subtree !== false) ?? true);
  const [audienceTypes, setAudienceTypes] = useState<string[]>(clause?.membershipTypes ?? []);
  const [critical, setCritical] = useState(initial.critical !== null);
  const [presentation, setPresentation] = useState<"modal" | "banner">(initial.critical?.presentation ?? "banner");
  const [requireAck, setRequireAck] = useState(initial.critical?.requireAck ?? true);
  const [remindMax, setRemindMax] = useState(String(initial.critical?.remindLaterMax ?? 3));
  const [deadline, setDeadline] = useState(toLocalInput(initial.critical?.deadlineAt ?? null, site.timeZone));
  const [dismissible, setDismissible] = useState(initial.critical?.dismissible ?? false);
  const [note, setNote] = useState("");
  const [publishAt, setPublishAt] = useState(toLocalInput(initial.publishAt, site.timeZone));
  const [expiresAt, setExpiresAt] = useState(toLocalInput(initial.expiresAt, site.timeZone));
  const [material, setMaterial] = useState(true);
  const [device, setDevice] = useState<"desktop" | "phone">("desktop");
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<Result>(null);

  const locked = item.inReview || item.status === "archived";
  const draft = drafts[tab]!;
  const setDraft = (patch: Partial<Draft>) => setDrafts((d) => ({ ...d, [tab]: { ...d[tab]!, ...patch } }));
  const units1 = [...units].sort((a, b) => a.level - b.level || unitName(a, viewer).localeCompare(unitName(b, viewer), viewer));

  function audience(): ContentAudience {
    if (everyone || (audienceUnits.length === 0 && audienceTypes.length === 0)) return { anyOf: [] };
    return {
      anyOf: [
        {
          ...(audienceUnits.length > 0 ? { orgUnits: audienceUnits.map((id) => ({ id, subtree })) } : {}),
          ...(audienceTypes.length > 0 ? { membershipTypes: audienceTypes } : {}),
        },
      ],
    };
  }

  function request() {
    const variants = Object.fromEntries(
      Object.entries(drafts)
        .filter(([l, d]) => l === item.primaryLocale || d.title.trim() !== "" || d.body.blocks.length > 0)
        .map(([l, d]) => [l, { title: d.title, summary: d.summary.trim() === "" ? null : d.summary, body: d.body }]),
    );
    return {
      ownerId: owner,
      backupOwnerId: backup,
      reviewDueOn: reviewOn || null,
      classification,
      pinned,
      featured,
      audience: audience(),
      variants,
      changeNote: note.trim() === "" ? null : note,
      version: item.version,
      critical:
        item.type !== "announcement"
          ? null
          : critical
            ? { presentation, requireAck: presentation === "modal" || requireAck, remindLaterMax: Number(remindMax), deadline: deadline || null, dismissible: presentation === "banner" && dismissible }
            : { presentation: "none" },
    };
  }

  async function call(action: string, url: string, method: string, body?: unknown, done?: string) {
    setBusy(action);
    setResult(null);
    try {
      const response = await fetch(url, {
        method,
        headers: body === undefined ? undefined : { "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (!response.ok) {
        setResult({ ok: false, messages: await problemMessages(response) });
        return false;
      }
      if (response.status !== 204) setItem((await response.json()) as ItemView);
      if (done) setResult({ ok: true, messages: [done] });
      router.refresh();
      return true;
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
      return false;
    } finally {
      setBusy(null);
    }
  }

  const base = `/api/v1/content/items/${encodeURIComponent(item.id)}`;
  const save = () => call("save", base, "PUT", request(), t("saved"));
  const go = async (action: "publish" | "submit") => {
    if (!locked && !(await call("save", base, "PUT", request()))) return;
    await call(action, `${base}/${action}`, "POST", { publishAt: publishAt || null, expiresAt: expiresAt || null, material }, t(action === "publish" ? "published" : "submitted"));
  };

  const toggle = (list: string[], value: string, on: boolean) => (on ? [...new Set([...list, value])] : list.filter((v) => v !== value));
  const preview = drafts[tab]!;

  return (
    <div className="content-editor-page">
      <p className="content-meta" data-testid="item-status">
        {t("statusLine", { status: t(`status.${item.status}`), version: item.draftVersion ?? item.publishedVersion ?? 0 })}
        {item.inReview ? <span className="content-badge">{t("inReview")}</span> : null}
        {item.critical ? <span className="content-badge">{t("criticalBadge")}</span> : null}
      </p>
      <nav aria-label={t("related")} className="content-actions">
        {item.publishedVersion ? <Link href={readHref(item.id)}>{t("openReader")}</Link> : null}
        <Link href={`/content/items/${encodeURIComponent(item.id)}/history`}>{t("history")}</Link>
        {item.critical ? <Link href={`/content/items/${encodeURIComponent(item.id)}/acknowledgements`}>{t("acknowledgements")}</Link> : null}
      </nav>
      <Notice result={result} />

      <section className="content-card" aria-labelledby={`${ids}-text`}>
        <h2 id={`${ids}-text`}>{t("text")}</h2>
        <div role="tablist" aria-label={t("languages")} className="content-tabs">
          {order.map((l) => (
            <button
              key={l}
              type="button"
              role="tab"
              id={`${ids}-tab-${l}`}
              aria-selected={tab === l}
              aria-controls={`${ids}-panel`}
              tabIndex={tab === l ? 0 : -1}
              className="content-tab"
              onClick={() => setTab(l)}
              onKeyDown={(e) => {
                if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
                const forward = (e.key === "ArrowRight") !== (document.dir === "rtl");
                const next = order[(order.indexOf(l) + (forward ? 1 : order.length - 1)) % order.length]!;
                setTab(next);
                document.getElementById(`${ids}-tab-${next}`)?.focus();
              }}
            >
              {t(`language.${l === "ar" ? "ar" : "en"}`)}
              {l === item.primaryLocale ? ` (${t("primary")})` : ""}
              {item.variants[l]?.outOfDate ? ` (${t("outOfDate")})` : ""}
            </button>
          ))}
        </div>
        <div role="tabpanel" id={`${ids}-panel`} aria-labelledby={`${ids}-tab-${tab}`} className="content-card-body" lang={tab} dir={tab === "ar" ? "rtl" : "ltr"}>
          <FormField label={t("title")} name="title" value={draft.title} disabled={locked} maxLength={200} onChange={(e) => setDraft({ title: e.target.value })} />
          <FormField label={t("summary")} name="summary" value={draft.summary} disabled={locked} maxLength={500} onChange={(e) => setDraft({ summary: e.target.value })} />
          <BlockEditor key={tab} label={t("body")} locale={tab} value={draft.body} onChange={(body) => setDraft({ body })} />
        </div>
      </section>

      <section className="content-card" aria-labelledby={`${ids}-settings`}>
        <h2 id={`${ids}-settings`}>{t("settings")}</h2>
        <PersonPicker label={t("owner")} value={owner} onChange={setOwner} readOnly={locked} />
        <PersonPicker label={t("backupOwner")} value={backup} onChange={setBackup} readOnly={locked} />
        <FormField label={t("reviewOn")} name="reviewOn" type="date" value={reviewOn} disabled={locked} onChange={(e) => setReviewOn(e.target.value)} />
        <div className="field">
          <label htmlFor={`${ids}-class`}>{t("classification")}</label>
          <select id={`${ids}-class`} value={classification} disabled={locked} onChange={(e) => setClassification(e.target.value)}>
            {classifications.map((c) => (
              <option key={c} value={c}>
                {t(`classificationValue.${c}`)}
              </option>
            ))}
          </select>
        </div>
        <label className="content-check">
          <input type="checkbox" checked={pinned} disabled={locked} onChange={(e) => setPinned(e.target.checked)} />
          {t("pinned")}
        </label>
        <label className="content-check">
          <input type="checkbox" checked={featured} disabled={locked} onChange={(e) => setFeatured(e.target.checked)} />
          {t("featured")}
        </label>
      </section>

      <fieldset className="content-card" disabled={locked}>
        <legend>{t("audience")}</legend>
        <p className="content-meta">{t("audienceHint")}</p>
        <label className="content-check">
          <input type="radio" name={`${ids}-who`} checked={everyone} onChange={() => setEveryone(true)} />
          {t("everyone")}
        </label>
        <label className="content-check">
          <input type="radio" name={`${ids}-who`} checked={!everyone} onChange={() => setEveryone(false)} />
          {t("targeted")}
        </label>
        {!everyone ? (
          <>
            <fieldset>
              <legend>{t("units")}</legend>
              <ul className="content-list content-units">
                {units1.map((u) => (
                  <li key={u.id}>
                    <label className="content-check">
                      <input type="checkbox" checked={audienceUnits.includes(u.id)} onChange={(e) => setAudienceUnits(toggle(audienceUnits, u.id, e.target.checked))} />
                      <bdi>{unitName(u, viewer)}</bdi>
                    </label>
                  </li>
                ))}
              </ul>
              <label className="content-check">
                <input type="checkbox" checked={subtree} onChange={(e) => setSubtree(e.target.checked)} />
                {t("subtree")}
              </label>
            </fieldset>
            <fieldset>
              <legend>{t("membershipTypes")}</legend>
              {membershipTypes.map((m) => (
                <label key={m} className="content-check">
                  <input type="checkbox" checked={audienceTypes.includes(m)} onChange={(e) => setAudienceTypes(toggle(audienceTypes, m, e.target.checked))} />
                  {t(`membership.${m}`)}
                </label>
              ))}
            </fieldset>
          </>
        ) : null}
      </fieldset>

      {item.type === "announcement" ? (
        <fieldset className="content-card" disabled={locked}>
          <legend>{t("critical")}</legend>
          <label className="content-check">
            <input type="checkbox" checked={critical} onChange={(e) => setCritical(e.target.checked)} />
            {t("criticalOn")}
          </label>
          {critical ? (
            <>
              <div className="field">
                <label htmlFor={`${ids}-presentation`}>{t("presentation")}</label>
                <select id={`${ids}-presentation`} value={presentation} onChange={(e) => setPresentation(e.target.value as "modal" | "banner")}>
                  <option value="banner">{t("presentationValue.banner")}</option>
                  <option value="modal">{t("presentationValue.modal")}</option>
                </select>
              </div>
              {presentation === "modal" ? (
                <>
                  <p className="content-meta">{t("modalHint")}</p>
                  <FormField label={t("remindLaterMax")} name="remindLaterMax" type="number" min={0} max={10} value={remindMax} onChange={(e) => setRemindMax(e.target.value)} />
                </>
              ) : (
                <label className="content-check">
                  <input type="checkbox" checked={requireAck} onChange={(e) => setRequireAck(e.target.checked)} />
                  {t("requireAck")}
                </label>
              )}
              <FormField label={t("deadline")} name="deadline" type="datetime-local" hint={t("siteTime", { zone: site.timeZone })} value={deadline} onChange={(e) => setDeadline(e.target.value)} />
              {presentation === "banner" ? (
                <label className="content-check">
                  <input type="checkbox" checked={dismissible} onChange={(e) => setDismissible(e.target.checked)} />
                  {t("dismissible")}
                </label>
              ) : null}
            </>
          ) : null}
        </fieldset>
      ) : null}

      <section className="content-card" aria-labelledby={`${ids}-save`}>
        <h2 id={`${ids}-save`}>{t("saveAndPublish")}</h2>
        <FormField label={t("changeNote")} name="changeNote" value={note} disabled={locked} maxLength={500} onChange={(e) => setNote(e.target.value)} />
        <FormField label={t("publishAt")} name="publishAt" type="datetime-local" hint={t("publishAtHint", { zone: site.timeZone })} value={publishAt} onChange={(e) => setPublishAt(e.target.value)} />
        <FormField label={t("expiresAt")} name="expiresAt" type="datetime-local" hint={t("siteTime", { zone: site.timeZone })} value={expiresAt} onChange={(e) => setExpiresAt(e.target.value)} />
        <label className="content-check">
          <input type="checkbox" checked={material} onChange={(e) => setMaterial(e.target.checked)} />
          {t("material")}
        </label>
        <div className="content-actions">
          <Button disabled={locked} busy={busy === "save"} busyLabel={t("saving")} onClick={() => void save()}>
            {t("save")}
          </Button>
          <Button variant="primary" disabled={item.inReview || item.status === "archived"} busy={busy === "publish"} busyLabel={t("publishing")} onClick={() => void go("publish")}>
            {t("publish")}
          </Button>
          <Button disabled={item.inReview || item.status === "archived"} busy={busy === "submit"} busyLabel={t("sending")} onClick={() => void go("submit")}>
            {t("submit")}
          </Button>
          {item.inReview ? (
            <Button busy={busy === "withdraw"} busyLabel={t("sending")} onClick={() => void call("withdraw", `${base}/withdraw`, "POST", undefined, t("withdrawn"))}>
              {t("withdraw")}
            </Button>
          ) : null}
          {item.status === "published" || item.status === "scheduled" ? (
            <Button variant="quiet" busy={busy === "unpublish"} busyLabel={t("sending")} onClick={() => void call("unpublish", `${base}/unpublish`, "POST", undefined, t("unpublished"))}>
              {t("unpublish")}
            </Button>
          ) : null}
          {item.status !== "archived" ? (
            <Button variant="destructive" busy={busy === "archive"} busyLabel={t("sending")} onClick={() => void call("archive", `${base}/archive`, "POST", undefined, t("archived"))}>
              {t("archive")}
            </Button>
          ) : null}
        </div>
      </section>

      <section className="content-card" aria-labelledby={`${ids}-preview`}>
        <h2 id={`${ids}-preview`}>{t("preview")}</h2>
        <div role="group" aria-label={t("device")} className="content-actions">
          {(["desktop", "phone"] as const).map((d) => (
            <Button key={d} variant="quiet" aria-pressed={device === d} onClick={() => setDevice(d)}>
              {t(`deviceValue.${d}`)}
            </Button>
          ))}
        </div>
        <p className="content-meta">{everyone ? t("previewEveryone") : t("previewTargeted", { units: audienceUnits.length, types: audienceTypes.length })}</p>
        <article className="content-preview" data-device={device} lang={tab} dir={tab === "ar" ? "rtl" : "ltr"} aria-label={t("previewOf", { title: preview.title })}>
          <h3>
            <bdi>{preview.title}</bdi>
          </h3>
          {preview.summary ? (
            <p className="content-summary">
              <bdi>{preview.summary}</bdi>
            </p>
          ) : null}
          <BodyView body={preview.body} embedTitle={t("embed")} />
        </article>
      </section>
    </div>
  );
}
