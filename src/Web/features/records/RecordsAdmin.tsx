"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { PersonPicker } from "@/features/services/PersonPicker";
import { problemMessages } from "@/lib/notifications/types";
import { documentHref, nameIn } from "@/lib/documents/types";
import {
  actions,
  triggers,
  type DispositionItem,
  type DispositionRecordView,
  type HoldDetail,
  type HoldView,
  type LabelView,
  type RecordsLibraryView,
  type RetentionAction,
  type RetentionTrigger,
} from "@/lib/records/types";
import "./records.css";

function Problems({ messages }: { messages: string[] }) {
  return messages.length > 0 ? (
    <div className="notice notice-error" role="alert">
      {messages.map((m) => (
        <p key={m}>{m}</p>
      ))}
    </div>
  ) : null;
}

/** Sends one change to the records API, refreshes the page when it worked, and shows the API's problems otherwise. */
function useSend() {
  const t = useTranslations("records");
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  async function send(url: string, method: "POST" | "PUT", body?: unknown): Promise<unknown> {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(url, {
        method,
        headers: body === undefined ? undefined : { "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (response.ok) {
        router.refresh();
        return response.status === 204 ? true : await response.json();
      }
      setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
    return null;
  }
  return { busy, messages, send };
}

type LabelDraft = { en: string; ar: string; months: string; trigger: RetentionTrigger; field: string; action: RetentionAction; status: "active" | "retired"; reapply: boolean };

const emptyDraft: LabelDraft = { en: "", ar: "", months: "12", trigger: "created", field: "", action: "review", status: "active", reapply: false };

/**
 * Retention labels (spec 011 FR-013): a period, the event that starts it and what happens when it ends. Editing a label
 * to a shorter period or another action reaches documents labelled before only when "apply to labelled documents" is on.
 */
export function LabelsPanel({ labels }: { labels: LabelView[] }) {
  const t = useTranslations("records");
  const locale = useLocale();
  const [editing, setEditing] = useState<LabelView | null>(null);
  const [draft, setDraft] = useState<LabelDraft>(emptyDraft);
  const [saved, setSaved] = useState(false);
  const { busy, messages, send } = useSend();

  function edit(label: LabelView) {
    setSaved(false);
    setEditing(label);
    setDraft({
      en: label.names.en ?? "",
      ar: label.names.ar ?? "",
      months: String(label.periodMonths),
      trigger: label.trigger,
      field: label.triggerField ?? "",
      action: label.action,
      status: label.status,
      reapply: false,
    });
  }

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setSaved(false);
    const names: Record<string, string> = { en: draft.en.trim() };
    if (draft.ar.trim()) names.ar = draft.ar.trim();
    const body = {
      names,
      periodMonths: Number(draft.months),
      trigger: draft.trigger,
      triggerField: draft.trigger === "field" ? draft.field.trim() : null,
      action: draft.action,
      status: editing ? draft.status : null,
      version: editing?.version ?? null,
      reapply: draft.reapply,
    };
    const done = editing ? await send(`/api/v1/records/labels/${encodeURIComponent(editing.id)}`, "PUT", body) : await send("/api/v1/records/labels", "POST", body);
    if (done) {
      setSaved(true);
      setEditing(null);
      setDraft(emptyDraft);
    }
  }

  return (
    <section className="records-card" aria-labelledby="records-labels">
      <h2 id="records-labels">{t("labels.title")}</h2>
      <p className="field-hint">{t("labels.intro")}</p>
      {labels.length === 0 ? (
        <p className="field-hint">{t("labels.empty")}</p>
      ) : (
        <ul className="records-list">
          {labels.map((l) => (
            <li key={l.id} data-testid="retention-label">
              <span className="records-name">
                <bdi>{nameIn(l.names, locale)}</bdi>
              </span>
              <span className="records-meta">{t("labels.summary", { months: l.periodMonths, trigger: t(`labels.trigger.${l.trigger}`), action: t(`labels.action.${l.action}`) })}</span>
              <span className="records-meta">{t("labels.documents", { count: l.documents })}</span>
              {l.status === "retired" ? <Badge tone="neutral">{t("labels.retired")}</Badge> : null}
              <Button variant="quiet" onClick={() => edit(l)}>
                {t("labels.edit", { name: nameIn(l.names, locale) })}
              </Button>
            </li>
          ))}
        </ul>
      )}
      <h3>{editing ? t("labels.editTitle", { name: nameIn(editing.names, locale) }) : t("labels.createTitle")}</h3>
      <Problems messages={messages} />
      {saved ? (
        <p className="notice notice-success" role="status">
          {t("labels.saved")}
        </p>
      ) : null}
      <form className="records-form" onSubmit={(e) => void save(e)}>
        <FormField label={t("labels.nameEn")} name="name-en" value={draft.en} onChange={(e) => setDraft({ ...draft, en: e.target.value })} required maxLength={120} />
        <FormField label={t("labels.nameAr")} name="name-ar" value={draft.ar} onChange={(e) => setDraft({ ...draft, ar: e.target.value })} maxLength={120} dir="rtl" lang="ar" />
        <FormField
          label={t("labels.months")}
          name="months"
          type="number"
          min={1}
          max={1200}
          value={draft.months}
          onChange={(e) => setDraft({ ...draft, months: e.target.value })}
          required
        />
        <SelectField
          label={t("labels.triggerLabel")}
          name="trigger"
          value={draft.trigger}
          onChange={(e) => setDraft({ ...draft, trigger: e.target.value as RetentionTrigger })}
          options={triggers.map((v) => ({ value: v, label: t(`labels.trigger.${v}`) }))}
        />
        {draft.trigger === "field" ? (
          <FormField
            label={t("labels.field")}
            hint={t("labels.fieldHint")}
            name="field"
            value={draft.field}
            onChange={(e) => setDraft({ ...draft, field: e.target.value })}
            required
            maxLength={64}
          />
        ) : null}
        <SelectField
          label={t("labels.actionLabel")}
          name="action"
          value={draft.action}
          onChange={(e) => setDraft({ ...draft, action: e.target.value as RetentionAction })}
          options={actions.map((v) => ({ value: v, label: t(`labels.action.${v}`) }))}
        />
        {editing ? (
          <>
            <Checkbox label={t("labels.retire")} checked={draft.status === "retired"} onChange={(e) => setDraft({ ...draft, status: e.target.checked ? "retired" : "active" })} />
            <Checkbox label={t("labels.reapply")} hint={t("labels.reapplyHint")} checked={draft.reapply} onChange={(e) => setDraft({ ...draft, reapply: e.target.checked })} />
          </>
        ) : null}
        <p className="records-actions">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("labels.saving")}>
            {editing ? t("labels.save") : t("labels.create")}
          </Button>
          {editing ? (
            <Button
              variant="quiet"
              onClick={() => {
                setEditing(null);
                setDraft(emptyDraft);
              }}
            >
              {t("labels.cancel")}
            </Button>
          ) : null}
        </p>
      </form>
    </section>
  );
}

/** Where labels apply (FR-013): a library, a document type of it, or both, in which case the longer period wins. */
export function ApplyPanel({ libraries, labels }: { libraries: RecordsLibraryView[]; labels: LabelView[] }) {
  const t = useTranslations("records");
  const locale = useLocale();
  const { messages, send } = useSend();
  const options = [{ value: "", label: t("apply.none") }, ...labels.filter((l) => l.status === "active").map((l) => ({ value: l.id, label: nameIn(l.names, locale) }))];
  const choose = (url: string, value: string) => void send(url, "PUT", { labelId: value || null });

  return (
    <section className="records-card" aria-labelledby="records-apply">
      <h2 id="records-apply">{t("apply.title")}</h2>
      <p className="field-hint">{t("apply.intro")}</p>
      <Problems messages={messages} />
      {libraries.length === 0 ? (
        <p className="field-hint">{t("apply.empty")}</p>
      ) : (
        <ul className="records-list records-apply">
          {libraries.map((library) => {
            const name = nameIn(library.names, locale);
            return (
              <li key={library.id} data-testid="records-library">
                <SelectField
                  label={t("apply.library", { name })}
                  name={`library-${library.id}`}
                  value={library.labelId ?? ""}
                  onChange={(e) => choose(`/api/v1/records/libraries/${encodeURIComponent(library.id)}/label`, e.target.value)}
                  options={options}
                />
                {library.types.map((type) => (
                  <SelectField
                    key={type.id}
                    label={t("apply.type", { name: nameIn(type.names, locale), library: name })}
                    name={`type-${type.id}`}
                    value={type.labelId ?? ""}
                    onChange={(e) => choose(`/api/v1/records/types/${encodeURIComponent(type.id)}/label`, e.target.value)}
                    options={options}
                  />
                ))}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

/** One document waiting for the records manager's decision: dispose of it, or keep it for more months. */
function DispositionRow({ item }: { item: DispositionItem }) {
  const t = useTranslations("records");
  const locale = useLocale();
  const format = useFormatter();
  const [months, setMonths] = useState("12");
  const { busy, messages, send } = useSend();
  const url = `/api/v1/records/disposition/${encodeURIComponent(item.documentId)}`;
  return (
    <li data-testid="disposition-item">
      <Link href={documentHref(item.documentId)} className="records-name">
        <bdi>{item.number ? `${item.number} ${item.name}` : item.name}</bdi>
      </Link>
      <span className="records-meta">
        <bdi>{nameIn(item.labelNames, locale)}</bdi>
      </span>
      {item.endedAt ? <span className="records-meta">{t("disposition.ended", { date: format.dateTime(new Date(item.endedAt), { dateStyle: "medium" }) })}</span> : null}
      {item.held ? <Badge tone="warning">{t("disposition.held")}</Badge> : null}
      <Problems messages={messages} />
      <div className="records-actions">
        <ConfirmAction
          label={t("disposition.dispose")}
          question={t("disposition.disposeQuestion", { name: item.name })}
          confirmLabel={t("disposition.disposeConfirm")}
          cancelLabel={t("disposition.cancel")}
          onConfirm={async () => {
            await send(url, "POST", { decision: "dispose" });
          }}
        />
        <FormField label={t("disposition.keepMonths")} name={`keep-${item.documentId}`} type="number" min={1} max={1200} value={months} onChange={(e) => setMonths(e.target.value)} />
        <Button busy={busy} busyLabel={t("disposition.saving")} onClick={() => void send(url, "POST", { decision: "keep", keepMonths: Number(months) })}>
          {t("disposition.keep")}
        </Button>
      </div>
    </li>
  );
}

/** The disposition review queue (US6 scenario 1) and the content-free record of what was disposed of (FR-014). */
export function DispositionPanel({ queue, records }: { queue: DispositionItem[]; records: DispositionRecordView[] }) {
  const t = useTranslations("records");
  const locale = useLocale();
  const format = useFormatter();
  return (
    <section className="records-card" aria-labelledby="records-disposition">
      <h2 id="records-disposition">{t("disposition.title")}</h2>
      <p className="field-hint">{t("disposition.intro")}</p>
      {queue.length === 0 ? (
        <p className="field-hint">{t("disposition.empty")}</p>
      ) : (
        <ul className="records-list">
          {queue.map((item) => (
            <DispositionRow key={item.documentId} item={item} />
          ))}
        </ul>
      )}
      <h3>{t("disposition.recordsTitle")}</h3>
      {records.length === 0 ? (
        <p className="field-hint">{t("disposition.noRecords")}</p>
      ) : (
        <ul className="records-list">
          {records.map((r) => (
            <li key={r.id} data-testid="disposition-record">
              <span className="records-name">
                <bdi>{r.number ?? t("disposition.noNumber")}</bdi>
              </span>
              <span className="records-meta">
                <bdi>{nameIn(r.labelNames, locale)}</bdi>
              </span>
              <span className="records-meta">{t(`disposition.mode.${r.mode}`)}</span>
              <span className="records-meta">{t("disposition.versions", { count: r.versions })}</span>
              <span className="records-meta">{format.dateTime(new Date(r.disposedAt), { dateStyle: "medium" })}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** The documents one hold covers, loaded when the records manager asks for them. */
function HeldDocuments({ hold }: { hold: HoldView }) {
  const t = useTranslations("records");
  const [detail, setDetail] = useState<HoldDetail | null>(null);
  const [failed, setFailed] = useState(false);
  async function load() {
    setFailed(false);
    try {
      const response = await fetch(`/api/v1/records/holds/${encodeURIComponent(hold.id)}`, { cache: "no-store" });
      if (response.ok) setDetail((await response.json()) as HoldDetail);
      else setFailed(true);
    } catch {
      setFailed(true);
    }
  }
  if (!detail) {
    return (
      <>
        <Button variant="quiet" onClick={() => void load()}>
          {t("holds.showDocuments", { name: hold.name })}
        </Button>
        {failed ? (
          <p className="field-error" role="alert">
            {t("holds.loadFailed")}
          </p>
        ) : null}
      </>
    );
  }
  return detail.documents.length === 0 ? (
    <p className="field-hint">{t("holds.noDocuments")}</p>
  ) : (
    <ul className="records-held" aria-label={t("holds.documentsOf", { name: hold.name })}>
      {detail.documents.map((d) => (
        <li key={d.id}>
          <Link href={documentHref(d.id)}>
            <bdi>{d.number ? `${d.number} ${d.name}` : d.name}</bdi>
          </Link>
        </li>
      ))}
    </ul>
  );
}

type Target = "custodian" | "library";

/**
 * Legal holds (US7, FR-015): by custodian with an optional date range and their audit records, or by library. Holds
 * cover TeamBeats libraries only. Their reasons and the documents they hold are shown to records managers only.
 */
export function HoldsPanel({ holds, libraries }: { holds: HoldView[]; libraries: RecordsLibraryView[] }) {
  const t = useTranslations("records");
  const locale = useLocale();
  const format = useFormatter();
  const [name, setName] = useState("");
  const [reason, setReason] = useState("");
  const [target, setTarget] = useState<Target>("custodian");
  const [custodian, setCustodian] = useState<string | null>(null);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [audit, setAudit] = useState(false);
  const [library, setLibrary] = useState(libraries[0]?.id ?? "");
  const { busy, messages, send } = useSend();
  const libraryName = (id: string | null) => nameIn(libraries.find((l) => l.id === id)?.names ?? {}, locale);

  async function create(event: React.FormEvent) {
    event.preventDefault();
    const body =
      target === "custodian"
        ? {
            name: name.trim(),
            reason: reason.trim(),
            custodianId: custodian,
            from: from ? new Date(`${from}T00:00:00`).toISOString() : null,
            to: to ? new Date(`${to}T23:59:59`).toISOString() : null,
            includeAudit: audit,
          }
        : { name: name.trim(), reason: reason.trim(), libraryId: library || null };
    if (await send("/api/v1/records/holds", "POST", body)) {
      setName("");
      setReason("");
      setCustodian(null);
      setFrom("");
      setTo("");
      setAudit(false);
    }
  }

  return (
    <section className="records-card" aria-labelledby="records-holds">
      <h2 id="records-holds">{t("holds.title")}</h2>
      <p className="field-hint">{t("holds.intro")}</p>
      <p className="notice notice-info">{t("holds.reach")}</p>
      {holds.length === 0 ? (
        <p className="field-hint">{t("holds.empty")}</p>
      ) : (
        <ul className="records-list">
          {holds.map((h) => (
            <li key={h.id} data-testid="legal-hold">
              <span className="records-name">
                <bdi>{h.name}</bdi>
              </span>
              <Badge tone={h.status === "active" ? "warning" : "neutral"}>{t(`holds.status.${h.status}`)}</Badge>
              <span className="records-meta">
                {h.custodianId ? t("holds.byCustodian") : h.libraryId ? t("holds.byLibrary", { name: libraryName(h.libraryId) }) : t("holds.byDocument")}
              </span>
              <span className="records-meta">{t("holds.documents", { count: h.documents })}</span>
              <span className="records-meta">{t("holds.placed", { date: format.dateTime(new Date(h.createdAt), { dateStyle: "medium" }) })}</span>
              <p className="records-reason">
                <bdi>{h.reason}</bdi>
              </p>
              <HeldDocuments hold={h} />
              {h.status === "active" ? (
                <ConfirmAction
                  label={t("holds.release")}
                  question={t("holds.releaseQuestion", { name: h.name })}
                  confirmLabel={t("holds.releaseConfirm")}
                  cancelLabel={t("holds.cancel")}
                  onConfirm={async () => {
                    await send(`/api/v1/records/holds/${encodeURIComponent(h.id)}/release`, "POST");
                  }}
                />
              ) : null}
            </li>
          ))}
        </ul>
      )}
      <h3>{t("holds.createTitle")}</h3>
      <Problems messages={messages} />
      <form className="records-form" onSubmit={(e) => void create(e)}>
        <FormField label={t("holds.name")} name="hold-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} />
        <TextAreaField label={t("holds.reason")} name="hold-reason" value={reason} onChange={(e) => setReason(e.target.value)} required maxLength={2000} />
        <SelectField
          label={t("holds.target")}
          name="hold-target"
          value={target}
          onChange={(e) => setTarget(e.target.value as Target)}
          options={[
            { value: "custodian", label: t("holds.targetCustodian") },
            { value: "library", label: t("holds.targetLibrary") },
          ]}
        />
        {target === "custodian" ? (
          <>
            <PersonPicker label={t("holds.custodian")} value={custodian} onChange={setCustodian} />
            <FormField label={t("holds.from")} name="hold-from" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
            <FormField label={t("holds.to")} name="hold-to" type="date" value={to} onChange={(e) => setTo(e.target.value)} />
            <Checkbox label={t("holds.includeAudit")} hint={t("holds.includeAuditHint")} checked={audit} onChange={(e) => setAudit(e.target.checked)} />
          </>
        ) : (
          <SelectField
            label={t("holds.library")}
            name="hold-library"
            value={library}
            onChange={(e) => setLibrary(e.target.value)}
            options={libraries.map((l) => ({ value: l.id, label: nameIn(l.names, locale) }))}
          />
        )}
        <p className="records-actions">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("holds.saving")}>
            {t("holds.create")}
          </Button>
        </p>
      </form>
    </section>
  );
}
