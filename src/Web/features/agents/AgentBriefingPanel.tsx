"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { problemMessages } from "@/lib/notifications/types";
import type { BriefingView } from "@/lib/agents/types";

const DAYS = [1, 2, 3, 4, 5, 6, 7] as const;

/**
 * A daily briefing from this agent (spec 020 US8): the person picks a time and days and subscribes, changes them, or
 * opts out. The API decides whether the briefing is offered; the browser's time zone is sent, and the tenant's is used
 * when the browser has none.
 */
export function AgentBriefingPanel({ view }: { view: BriefingView }) {
  const t = useTranslations("agents.briefing");
  const format = useFormatter();
  const router = useRouter();
  const ids = useId();
  const current = view.subscription;
  const [time, setTime] = useState(current?.time ?? "08:00");
  const [days, setDays] = useState<number[]>(current?.days ?? [1, 2, 3, 4, 5]);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function send(method: "PUT" | "DELETE") {
    setBusy(true);
    setMessages([]);
    try {
      const zone = Intl.DateTimeFormat().resolvedOptions().timeZone || view.defaultTimeZone;
      const response = await fetch(`/api/v1/agents/${view.agentId}/briefing`, {
        method,
        headers: method === "PUT" ? { "content-type": "application/json" } : undefined,
        body: method === "PUT" ? JSON.stringify({ time, days, timeZone: zone }) : undefined,
      });
      if (response.ok) {
        router.refresh();
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    }
    setBusy(false);
  }

  // A Monday in January 2024 gives each weekday's name in the person's language.
  const dayName = (iso: number) => format.dateTime(new Date(Date.UTC(2024, 0, iso)), { weekday: "long", timeZone: "UTC" });

  return (
    <section className="agents-section" aria-labelledby={`${ids}-title`} data-testid="agent-briefing">
      <h2 id={`${ids}-title`}>{t("title")}</h2>
      {current ? (
        <p>{t("subscribed", { next: format.dateTime(new Date(current.nextRunAt), { dateStyle: "medium", timeStyle: "short" }) })}</p>
      ) : view.offered ? (
        <p>{t("offer")}</p>
      ) : null}
      {!view.offered && current ? <p>{t("notOffered")}</p> : null}
      {view.offered ? (
        <>
          <div className="field">
            <label htmlFor={`${ids}-time`}>{t("time")}</label>
            <input id={`${ids}-time`} type="time" value={time} onChange={(e) => setTime(e.target.value)} />
          </div>
          <fieldset className="agents-checks">
            <legend>{t("days")}</legend>
            {DAYS.map((d) => (
              <Checkbox
                key={d}
                label={dayName(d)}
                checked={days.includes(d)}
                onChange={(e) => setDays(e.target.checked ? [...days.filter((x) => x !== d), d].sort() : days.filter((x) => x !== d))}
              />
            ))}
          </fieldset>
          <div className="agents-market-actions">
            <Button disabled={busy || days.length === 0} onClick={() => send("PUT")}>
              {current ? t("change") : t("subscribe")}
            </Button>
            {current ? (
              <Button variant="secondary" disabled={busy} onClick={() => send("DELETE")}>
                {t("unsubscribe")}
              </Button>
            ) : null}
          </div>
        </>
      ) : current ? (
        <div>
          <Button variant="secondary" disabled={busy} onClick={() => send("DELETE")}>
            {t("unsubscribe")}
          </Button>
        </div>
      ) : null}
      {messages.map((m) => (
        <p key={m} role="alert" className="field-error">
          {m}
        </p>
      ))}
    </section>
  );
}
