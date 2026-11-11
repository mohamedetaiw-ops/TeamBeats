"use client";

import { useEffect, useId, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { PersonPicker } from "@/features/services/PersonPicker";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import { todayIso, type LeavePreview, type LeaveRequest, type LeaveType } from "@/lib/leave";

type Upload = { id: string; fileName: string; scanState: string };

/**
 * The leave request form (spec 022 US1 scenarios 1 to 4). While the person types, the API answers with the working
 * days the request takes, the balance per leave year before and after, who else on the team is away and whether a
 * document is needed. The browser counts nothing itself. The same idempotency key goes with every try of one submit,
 * so a double click files one request.
 */
export function LeaveRequestForm({ types, onSubmitted }: { types: LeaveType[]; onSubmitted: (request: LeaveRequest) => void }) {
  const t = useTranslations("leave.form");
  const locale = useLocale();
  const format = useFormatter();
  const fileId = useId();
  const [typeKey, setTypeKey] = useState(types[0]?.key ?? "");
  const [start, setStart] = useState(todayIso());
  const [end, setEnd] = useState(todayIso());
  const [startHalf, setStartHalf] = useState(false);
  const [endHalf, setEndHalf] = useState(false);
  const [hours, setHours] = useState("");
  const [comment, setComment] = useState("");
  const [replacement, setReplacement] = useState<string | null>(null);
  const [attached, setAttached] = useState<Upload | null>(null);
  const [preview, setPreview] = useState<LeavePreview | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [key, setKey] = useState(() => crypto.randomUUID());
  const type = types.find((x) => x.key === typeKey) ?? null;
  const byHour = type?.unit === "hour";
  const lastDay = byHour || end < start ? start : end;
  const hourValue = byHour && hours.trim() ? Number(hours) : null;
  const body = { typeKey, start, end: lastDay, startHalf, endHalf, hours: hourValue, comment: comment.trim() || null, replacementUserId: replacement, documentId: attached?.id ?? null };

  useEffect(() => {
    if (!typeKey || !start || !lastDay) return;
    let live = true;
    const timer = setTimeout(() => {
      void (async () => {
        try {
          const response = await fetch("/api/v1/hr/leave/preview", {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ typeKey, start, end: lastDay, startHalf, endHalf, hours: hourValue }),
          });
          if (!live) return;
          if (response.ok) {
            setPreview((await response.json()) as LeavePreview);
            setMessages([]);
          } else {
            setPreview(null);
            setMessages(await problemMessages(response));
          }
        } catch {
          if (live) setMessages([t("previewFailed")]);
        }
      })();
    }, 300);
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [typeKey, start, lastDay, startHalf, endHalf, hourValue, t]);

  const date = (d: string) => format.dateTime(new Date(`${d}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  const number = (n: number) => format.number(n, { maximumFractionDigits: 2 });

  async function upload(files: FileList | null) {
    const file = files?.[0];
    if (!file) return;
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/hr/leave/documents?name=${encodeURIComponent(file.name)}`, {
        method: "POST",
        headers: { "content-type": file.type || "application/octet-stream" },
        body: file,
      });
      if (response.ok) setAttached((await response.json()) as Upload);
      else setMessages(await problemMessages(response));
    } catch {
      setMessages([t("uploadFailed")]);
    }
  }

  async function submit() {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/hr/leave/requests", {
        method: "POST",
        headers: { "content-type": "application/json", "Idempotency-Key": key },
        body: JSON.stringify(body),
      });
      if (response.ok) {
        const request = (await response.json()) as LeaveRequest;
        setKey(crypto.randomUUID());
        setComment("");
        setReplacement(null);
        setAttached(null);
        onSubmitted(request);
      } else {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  if (types.length === 0) return <p className="field-hint">{t("noTypes")}</p>;
  const blocked = (preview?.problems.length ?? 0) > 0 || (preview?.documentRequired === true && !attached);

  return (
    <form
      className="leave-form"
      onSubmit={(e) => {
        e.preventDefault();
        void submit();
      }}
    >
      <SelectField
        label={t("type")}
        name="typeKey"
        value={typeKey}
        onChange={(e) => setTypeKey(e.target.value)}
        options={types.map((x) => ({ value: x.key, label: pick(x.names, locale, x.key) }))}
      />
      <div className="leave-row">
        <FormField label={t("start")} name="start" type="date" value={start} onChange={(e) => setStart(e.target.value)} required />
        {byHour ? (
          <FormField label={t("hours")} name="hours" type="number" min={0.25} max={24} step={0.25} value={hours} onChange={(e) => setHours(e.target.value)} required />
        ) : (
          <FormField label={t("end")} name="end" type="date" value={end} min={start} onChange={(e) => setEnd(e.target.value)} required />
        )}
      </div>
      {type?.halfDays && !byHour ? (
        <div className="leave-row">
          <Checkbox label={t("startHalf")} name="startHalf" checked={startHalf} onChange={(e) => setStartHalf(e.target.checked)} />
          <Checkbox label={t("endHalf")} name="endHalf" checked={endHalf} onChange={(e) => setEndHalf(e.target.checked)} />
        </div>
      ) : null}

      {preview ? (
        <section className="leave-preview" aria-live="polite" aria-label={t("previewLabel")}>
          <p className="leave-total">{t("takes", { days: number(preview.days), unit: preview.unit })}</p>
          <ul className="leave-years">
            {preview.years.map((y) => (
              <li key={y.leaveYear}>
                {t("yearLine", { year: String(y.leaveYear), available: number(y.available), after: number(y.after) })}
              </li>
            ))}
          </ul>
          {preview.overlap.length > 0 ? (
            <div>
              <p className="leave-subhead">{t("overlap")}</p>
              <ul className="leave-overlap">
                {preview.overlap.map((o) => (
                  <li key={o.userId}>
                    <bdi>{o.displayName}</bdi>{" "}
                    <span className="field-hint">
                      {t("overlapDates", { count: o.dates.length, first: date(o.dates[0] ?? start), status: o.status })}
                    </span>
                  </li>
                ))}
              </ul>
            </div>
          ) : (
            <p className="field-hint">{t("noOverlap")}</p>
          )}
          {preview.problems.length > 0 ? (
            <Alert tone="warning" title={t("cannot")}>
              <ul>
                {preview.problems.map((p) => (
                  <li key={p.key}>{p.message}</li>
                ))}
              </ul>
            </Alert>
          ) : null}
        </section>
      ) : null}

      {preview?.documentRequired ? (
        <div className="field">
          <label htmlFor={fileId}>{t("document")}</label>
          <input id={fileId} type="file" accept="application/pdf,image/png,image/jpeg" onChange={(e) => void upload(e.target.files)} />
          <p className="field-hint">{attached ? t("documentChosen", { name: attached.fileName }) : t("documentHint")}</p>
        </div>
      ) : null}

      <PersonPicker label={t("replacement")} value={replacement} onChange={setReplacement} />
      <TextAreaField label={t("comment")} name="comment" value={comment} maxLength={1000} onChange={(e) => setComment(e.target.value)} />

      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <div className="leave-actions">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("submitting")} disabled={blocked || !preview}>
          {t("submit")}
        </Button>
      </div>
    </form>
  );
}
