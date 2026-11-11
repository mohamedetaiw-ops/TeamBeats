"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import Link from "next/link";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField } from "@/components/ui/FormField";
import { PersonPicker } from "@/features/services/PersonPicker";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import { JOURNEY_STATUSES, type Journey, type JourneyStatus, type JourneySummary, type JourneyTemplate } from "@/lib/journeys";
import "./journeys.css";

const statusTone = (s: JourneyStatus) => (s === "active" ? "info" : s === "completed" ? "success" : "neutral");

/**
 * The journeys dashboard (spec 022 US4 scenarios 6 and 7): journeys of the units the HR officer covers, with progress,
 * open and overdue steps, and a form to start a journey by hand. The API filters what the officer may see.
 */
export function JourneyDashboard({ initial, templates }: { initial: JourneySummary[]; templates: JourneyTemplate[] }) {
  const t = useTranslations("journeys.dashboard");
  const locale = useLocale();
  const format = useFormatter();
  const [rows, setRows] = useState(initial);
  const [status, setStatus] = useState<JourneyStatus | "">("active");
  const [overdue, setOverdue] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const active = templates.filter((x) => x.active);
  const [templateKey, setTemplateKey] = useState(active[0]?.key ?? "");
  const [person, setPerson] = useState<string | null>(null);
  const [buddy, setBuddy] = useState<string | null>(null);
  const [anchor, setAnchor] = useState("");
  const [busy, setBusy] = useState(false);

  const date = (d: string) => format.dateTime(new Date(`${d}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });

  async function load(nextStatus: JourneyStatus | "", nextOverdue: boolean) {
    setStatus(nextStatus);
    setOverdue(nextOverdue);
    const query = new URLSearchParams();
    if (nextStatus) query.set("status", nextStatus);
    if (nextOverdue) query.set("overdue", "true");
    const response = await fetch(`/api/v1/hr/journeys?${query.toString()}`, { cache: "no-store" });
    if (response.ok) {
      setRows((await response.json()) as JourneySummary[]);
    } else {
      const list = await problemMessages(response);
      setMessages(list.length > 0 ? list : [t("failed")]);
    }
  }

  async function start() {
    setBusy(true);
    setMessages([]);
    setNotice(null);
    try {
      const response = await fetch("/api/v1/hr/journeys", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ templateKey, userId: person, anchorDate: anchor || null, buddyUserId: buddy }),
      });
      if (response.ok) {
        const created = (await response.json()) as Journey;
        setNotice(t("started", { name: created.journey.person.displayName }));
        setPerson(null);
        setBuddy(null);
        await load(status, overdue);
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
    <section className="journeys-section" aria-labelledby="journeys-dashboard">
      <h2 id="journeys-dashboard">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
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
      <div className="journeys-row">
        <SelectField
          label={t("status")}
          name="journeyStatus"
          value={status}
          onChange={(e) => void load(e.target.value as JourneyStatus | "", overdue)}
          options={[{ value: "", label: t("all") }, ...JOURNEY_STATUSES.map((s) => ({ value: s, label: t(`statuses.${s}`) }))]}
        />
        <Checkbox label={t("overdueOnly")} name="journeyOverdue" checked={overdue} onChange={(e) => void load(status, e.target.checked)} />
      </div>
      {rows.length === 0 ? (
        <p className="field-hint">{t("empty")}</p>
      ) : (
        <div className="journeys-table">
          <table>
            <caption className="visually-hidden">{t("title")}</caption>
            <thead>
              <tr>
                <th scope="col">{t("person")}</th>
                <th scope="col">{t("template")}</th>
                <th scope="col">{t("anchor")}</th>
                <th scope="col">{t("progress")}</th>
                <th scope="col">{t("statusColumn")}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  <td>
                    <Link href={`/hr/journeys/${r.id}`}>
                      <bdi>{r.person.displayName}</bdi>
                    </Link>
                  </td>
                  <td>
                    <bdi>{pick(r.templateNames, locale, r.templateKey)}</bdi>
                  </td>
                  <td>{date(r.anchorDate)}</td>
                  <td>
                    {t("progressValue", { done: r.done, steps: r.steps })}
                    {r.overdue > 0 ? (
                      <>
                        {" "}
                        <Badge tone="danger">{t("overdue", { count: r.overdue })}</Badge>
                      </>
                    ) : null}
                  </td>
                  <td>
                    <Badge tone={statusTone(r.status)}>{t(`statuses.${r.status}`)}</Badge>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <h3>{t("startTitle")}</h3>
      {active.length === 0 ? (
        <p className="field-hint">{t("noTemplates")}</p>
      ) : (
        <form
          className="journeys-form"
          onSubmit={(e) => {
            e.preventDefault();
            void start();
          }}
        >
          <div className="journeys-row">
            <SelectField label={t("template")} name="startTemplate" value={templateKey} onChange={(e) => setTemplateKey(e.target.value)} options={active.map((x) => ({ value: x.key, label: pick(x.names, locale, x.key) }))} />
            <FormField label={t("anchor")} hint={t("anchorHint")} name="startAnchor" type="date" required value={anchor} onChange={(e) => setAnchor(e.target.value)} />
          </div>
          <PersonPicker label={t("person")} value={person} onChange={setPerson} />
          <PersonPicker label={t("buddy")} value={buddy} onChange={setBuddy} />
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")} disabled={!person}>
            {t("start")}
          </Button>
        </form>
      )}
    </section>
  );
}
