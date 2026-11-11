"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { PersonPicker } from "@/features/services/PersonPicker";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import { LEDGER_REASONS, type LedgerEntry, type LeaveType } from "@/lib/leave";
import "./leave.css";

/**
 * HR adjustments (spec 022 section 14): an HR officer picks a person they cover, sees their ledger, and adds or removes
 * days with a reason. The API decides who may see and change whom; when HR keeps balances it refuses and says so.
 */
export function LeaveAdjustments({ types }: { types: LeaveType[] }) {
  const t = useTranslations("leave.adjustments");
  const locale = useLocale();
  const format = useFormatter();
  const [person, setPerson] = useState<string | null>(null);
  const [entries, setEntries] = useState<LedgerEntry[] | null>(null);
  const [typeKey, setTypeKey] = useState(types[0]?.key ?? "");
  const [year, setYear] = useState(String(new Date().getFullYear()));
  const [delta, setDelta] = useState("");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  const number = (n: number) => format.number(n, { maximumFractionDigits: 2, signDisplay: "exceptZero" });
  const when = (at: string) => format.dateTime(new Date(at), { dateStyle: "medium", timeStyle: "short" });
  const typeName = (key: string) => {
    const type = types.find((x) => x.key === key);
    return type ? pick(type.names, locale, key) : key;
  };

  async function load(id: string | null) {
    setPerson(id);
    setEntries(null);
    setNotice(null);
    setMessages([]);
    if (!id) return;
    const response = await fetch(`/api/v1/hr/leave/ledger/${id}`, { cache: "no-store" });
    if (response.ok) {
      setEntries((await response.json()) as LedgerEntry[]);
    } else {
      const list = await problemMessages(response);
      setMessages(list.length > 0 ? list : [t("failed")]);
    }
  }

  async function adjust() {
    if (!person) return;
    setBusy(true);
    setNotice(null);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/hr/leave/adjustments", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ userId: person, typeKey, leaveYear: Number(year) || null, delta: Number(delta), note }),
      });
      if (response.ok) {
        setDelta("");
        setNote("");
        setNotice(t("saved"));
        const list = await fetch(`/api/v1/hr/leave/ledger/${person}`, { cache: "no-store" });
        if (list.ok) setEntries((await list.json()) as LedgerEntry[]);
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

  return (
    <section className="leave-section" aria-labelledby="leave-adjustments">
      <h2 id="leave-adjustments">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      <PersonPicker label={t("person")} value={person} onChange={(id) => void load(id)} />
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
      {person && entries ? (
        <>
          <form
            className="leave-form"
            onSubmit={(e) => {
              e.preventDefault();
              void adjust();
            }}
          >
            <div className="leave-row">
              <SelectField label={t("type")} name="adjustType" value={typeKey} onChange={(e) => setTypeKey(e.target.value)} options={types.map((x) => ({ value: x.key, label: pick(x.names, locale, x.key) }))} />
              <FormField label={t("year")} name="adjustYear" type="number" min={2000} max={2100} value={year} onChange={(e) => setYear(e.target.value)} />
              <FormField label={t("delta")} hint={t("deltaHint")} name="adjustDelta" type="number" step={0.25} inputMode="decimal" required value={delta} onChange={(e) => setDelta(e.target.value)} />
            </div>
            <FormField label={t("note")} name="adjustNote" required maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
              {t("save")}
            </Button>
          </form>
          {entries.length === 0 ? (
            <p className="field-hint">{t("empty")}</p>
          ) : (
            <div className="leave-table">
              <table>
                <caption className="visually-hidden">{t("ledger")}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t("at")}</th>
                    <th scope="col">{t("type")}</th>
                    <th scope="col">{t("year")}</th>
                    <th scope="col">{t("change")}</th>
                    <th scope="col">{t("reason")}</th>
                  </tr>
                </thead>
                <tbody>
                  {entries.map((e) => (
                    <tr key={e.id}>
                      <td>{when(e.at)}</td>
                      <td>
                        <bdi>{typeName(e.typeKey)}</bdi>
                      </td>
                      <td>{e.leaveYear}</td>
                      <td data-number>
                        <bdi dir="ltr">{number(e.delta)}</bdi>
                      </td>
                      <td>
                        {t(`reasons.${(LEDGER_REASONS as readonly string[]).includes(e.reason) ? e.reason : "other"}`)}
                        {e.note ? (
                          <span className="field-hint">
                            {" "}
                            <bdi>{e.note}</bdi>
                          </span>
                        ) : null}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      ) : null}
    </section>
  );
}
