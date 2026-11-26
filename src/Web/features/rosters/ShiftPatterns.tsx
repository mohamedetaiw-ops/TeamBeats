"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, FormPanel, SelectField } from "@/components/ui/FormField";
import { TimeField } from "@/components/ui/Inputs";
import { problemMessages } from "@/lib/notifications/types";
import { SHIFT_TONES, patternName, type ShiftPattern, type ShiftTone } from "@/lib/rosters";
import "./rosters.css";

const EMPTY = { en: "", ar: "", start: "08:00", end: "16:00", breakMinutes: "30", tone: "info" as ShiftTone };

/**
 * The organization's shift patterns (spec 022 FR-029), kept by HR officers of the whole organization: a name in each
 * language, the hours, the break and a colour. A pattern in use is turned off rather than deleted. The API decides who may.
 */
export function ShiftPatterns({ initial }: { initial: ShiftPattern[] }) {
  const t = useTranslations("rosters.patterns");
  const locale = useLocale();
  const [patterns, setPatterns] = useState(initial);
  const [draft, setDraft] = useState(EMPTY);
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function send(url: string, method: "POST" | "PUT", body: unknown, key: string, done: string) {
    setBusy(key);
    setNotice(null);
    setMessages([]);
    try {
      const response = await fetch(url, { method, headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      if (!response.ok) {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [t("failed")]);
        return false;
      }
      const saved = (await response.json()) as ShiftPattern;
      setPatterns((all) => [...all.filter((p) => p.id !== saved.id), saved].sort((a, b) => a.start.localeCompare(b.start)));
      setNotice(done);
      return true;
    } catch {
      setMessages([t("failed")]);
      return false;
    } finally {
      setBusy(null);
    }
  }

  async function add(event: React.FormEvent) {
    event.preventDefault();
    const names: Record<string, string> = { en: draft.en };
    if (draft.ar) names.ar = draft.ar;
    const ok = await send(
      "/api/v1/hr/shift-patterns",
      "POST",
      { names, start: draft.start, end: draft.end, breakMinutes: Number(draft.breakMinutes), tone: draft.tone },
      "add",
      t("added"),
    );
    if (ok) setDraft(EMPTY);
  }

  const toggle = (p: ShiftPattern) =>
    void send(`/api/v1/hr/shift-patterns/${p.id}`, "PUT", { names: p.names, start: p.start, end: p.end, breakMinutes: p.breakMinutes, tone: p.tone, active: !p.active }, p.id,
      p.active ? t("turnedOff") : t("turnedOn"));

  return (
    <div className="rosters">
      <h2>{t("title")}</h2>
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
      <DataTable<ShiftPattern>
        caption={t("caption")}
        rows={patterns}
        rowKey={(r) => r.id}
        density="compact"
        emptyTitle={t("empty")}
        columns={[
          { key: "name", header: t("name"), rowHeader: true, cell: (r) => <Badge tone={r.tone}>{patternName(r, locale)}</Badge> },
          { key: "hours", header: t("hours"), cell: (r) => <bdi>{t("hoursRange", { start: r.start, end: r.end })}</bdi> },
          { key: "break", header: t("breakMinutes"), cell: (r) => r.breakMinutes },
          { key: "active", header: t("state"), cell: (r) => (r.active ? t("active") : t("inactive")) },
          {
            key: "action",
            header: t("action"),
            cell: (r) => (
              <Button size="sm" busy={busy === r.id} busyLabel={t("saving")} onClick={() => toggle(r)}>
                {r.active ? t("turnOff") : t("turnOn")}
              </Button>
            ),
          },
        ]}
      />
      <FormPanel as="form" aria-labelledby="shift-pattern-add" onSubmit={(e) => void add(e)}>
        <h3 id="shift-pattern-add">{t("add")}</h3>
        <div className="rosters-row">
          <FormField label={t("nameEn")} name="pattern-en" value={draft.en} maxLength={60} required onChange={(e) => setDraft({ ...draft, en: e.target.value })} />
          <FormField label={t("nameAr")} name="pattern-ar" value={draft.ar} maxLength={60} dir="rtl" onChange={(e) => setDraft({ ...draft, ar: e.target.value })} />
        </div>
        <div className="rosters-row">
          <TimeField label={t("start")} name="pattern-start" value={draft.start} required onChange={(e) => setDraft({ ...draft, start: e.target.value })} />
          <TimeField label={t("end")} name="pattern-end" value={draft.end} required onChange={(e) => setDraft({ ...draft, end: e.target.value })} />
          <FormField label={t("breakMinutes")} name="pattern-break" type="number" min={0} max={240} value={draft.breakMinutes}
            onChange={(e) => setDraft({ ...draft, breakMinutes: e.target.value })} />
          <SelectField label={t("tone")} name="pattern-tone" value={draft.tone} options={SHIFT_TONES.map((x) => ({ value: x, label: t(`tones.${x}`) }))}
            onChange={(e) => setDraft({ ...draft, tone: e.target.value as ShiftTone })} />
        </div>
        <div className="rosters-row">
          <Button type="submit" variant="primary" busy={busy === "add"} busyLabel={t("saving")}>
            {t("addButton")}
          </Button>
        </div>
      </FormPanel>
    </div>
  );
}
