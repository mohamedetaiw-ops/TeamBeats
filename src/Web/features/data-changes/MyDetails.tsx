"use client";

import Link from "next/link";
import { useEffect, useId, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { dataChangeTone, type DataChange, type DataChangeGroup } from "@/lib/data-changes";
import "./data-changes.css";

type Upload = { id: string; fileName: string };

/**
 * Asking HR to change my details (spec 022 US6): one group at a time, with a supporting document where the
 * organization asks for one. HR approves, then the change is written to the HR system; the profile keeps HR's value
 * until then. The API checks every value and decides which groups are offered.
 */
export function MyDetails({ groups }: { groups: DataChangeGroup[] }) {
  const t = useTranslations("data_changes");
  const format = useFormatter();
  const fileId = useId();
  const [groupKey, setGroupKey] = useState(groups[0]?.key ?? "");
  const [values, setValues] = useState<Record<string, string>>({});
  const [reason, setReason] = useState("");
  const [attached, setAttached] = useState<Upload | null>(null);
  const [list, setList] = useState<DataChange[] | null>(null);
  const [reload, setReload] = useState(0);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/me/data-changes", { cache: "no-store" });
        if (live && response.ok) setList((await response.json()) as DataChange[]);
      } catch {
        if (live) setList([]);
      }
    })();
    return () => {
      live = false;
    };
  }, [reload]);

  const group = groups.find((g) => g.key === groupKey);

  async function upload(files: FileList | null) {
    const file = files?.[0];
    if (!file) return;
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/me/data-changes/documents?name=${encodeURIComponent(file.name)}`, {
        method: "POST",
        headers: { "content-type": file.type || "application/octet-stream" },
        body: file,
      });
      if (response.ok) setAttached((await response.json()) as Upload);
      else setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    }
  }

  async function send() {
    setBusy(true);
    setNotice(null);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/me/data-changes", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ group: groupKey, values, reason: reason || null, documentId: attached?.id ?? null }),
      });
      if (!response.ok) {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [t("failed")]);
        return;
      }
      setNotice(t("sent"));
      setValues({});
      setReason("");
      setAttached(null);
      setReload((n) => n + 1);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="data-changes">
      <section className="data-changes-section" aria-labelledby="data-changes-ask">
        <h2 id="data-changes-ask">{t("ask")}</h2>
        <p className="field-hint">{t("askIntro")}</p>
        {notice ? (
          <p className="notice" role="status">
            {notice}
          </p>
        ) : null}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {groups.length === 0 ? (
          <p className="field-hint">{t("noGroups")}</p>
        ) : (
          <>
            <SelectField
              label={t("group")}
              name="data-change-group"
              value={groupKey}
              onChange={(e) => {
                setGroupKey(e.target.value);
                setValues({});
                setAttached(null);
              }}
              options={groups.map((g) => ({ value: g.key, label: t(`groups.${g.key}`) }))}
            />
            {group?.sensitive ? (
              <Alert tone="info" title={t("sensitiveTitle")}>
                {t("sensitive")}
              </Alert>
            ) : null}
            {group?.fields.map((f) =>
              f.choices ? (
                <SelectField
                  key={f.key}
                  label={t(`fields.${f.key}`)}
                  name={`data-change-${f.key}`}
                  value={values[f.key] ?? ""}
                  onChange={(e) => setValues((all) => ({ ...all, [f.key]: e.target.value }))}
                  options={[{ value: "", label: t("choose") }, ...f.choices.map((c) => ({ value: c, label: t(`choices.${c}`) }))]}
                />
              ) : (
                <FormField
                  key={f.key}
                  label={t(`fields.${f.key}`)}
                  name={`data-change-${f.key}`}
                  value={values[f.key] ?? ""}
                  maxLength={f.key === "iban" ? 42 : f.maxLength}
                  inputMode={f.key === "dependants_count" ? "numeric" : f.key === "phone" ? "tel" : undefined}
                  dir={f.key === "iban" || f.key === "phone" ? "ltr" : undefined}
                  hint={f.required ? undefined : t("optional")}
                  onChange={(e) => setValues((all) => ({ ...all, [f.key]: e.target.value }))}
                />
              ),
            )}
            {group?.document ? (
              <div className="field">
                <label htmlFor={fileId}>{t("document")}</label>
                <input id={fileId} type="file" accept="application/pdf,image/png,image/jpeg" onChange={(e) => void upload(e.target.files)} />
                <p className="field-hint">{attached ? t("documentChosen", { name: attached.fileName }) : t("documentHint")}</p>
              </div>
            ) : null}
            <TextAreaField label={t("reason")} name="data-change-reason" value={reason} maxLength={500} rows={3} onChange={(e) => setReason(e.target.value)} />
            <div className="data-changes-row">
              <Button variant="primary" busy={busy} busyLabel={t("sending")} disabled={!groupKey} onClick={() => void send()}>
                {t("send")}
              </Button>
            </div>
          </>
        )}
      </section>

      <section className="data-changes-section" aria-labelledby="data-changes-mine">
        <h2 id="data-changes-mine">{t("mine")}</h2>
        {list && list.length > 0 ? (
          <ul className="data-changes-list">
            {list.map((c) => (
              <li key={c.id}>
                <Link href={`/my-details/requests/${c.id}`}>{t(`groups.${c.group}`)}</Link>
                <span className="field-hint">{format.dateTime(new Date(c.createdAt), { dateStyle: "medium" })}</span>
                <Badge tone={dataChangeTone[c.status]}>{t(`statuses.${c.status}`)}</Badge>
              </li>
            ))}
          </ul>
        ) : (
          <p className="field-hint">{t("none")}</p>
        )}
      </section>
    </div>
  );
}
