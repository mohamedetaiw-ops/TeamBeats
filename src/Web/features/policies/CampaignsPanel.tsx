"use client";

import Link from "next/link";
import { useId, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { complianceHref, type CampaignView } from "@/lib/policies/types";
import { Button } from "@/components/ui/Button";
import "./policies.css";

/**
 * Attestation campaigns of a policy (spec 025 US2): launch one for the version in force with a deadline, reminder days
 * and the late joiner period, follow its counts, remind everyone who still owes, and close it. Leaving a field empty
 * takes the default the API applies.
 */
export function CampaignsPanel({
  policyId,
  initial,
  canLaunch,
  focus,
}: {
  policyId: string;
  initial: CampaignView[];
  canLaunch: boolean;
  focus: string | null;
}) {
  const t = useTranslations("policies.campaigns");
  const format = useFormatter();
  const ids = useId();
  const [campaigns, setCampaigns] = useState(initial);
  const [deadline, setDeadline] = useState("");
  const [reminders, setReminders] = useState("");
  const [lateDays, setLateDays] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [status, setStatus] = useState<string | null>(null);
  const base = `/api/v1/policies/${encodeURIComponent(policyId)}/campaigns`;

  async function call(key: string, url: string, body: unknown, done: (c: CampaignView) => void) {
    setBusy(key);
    setMessages([]);
    setStatus(null);
    try {
      const response = await fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      done((await response.json()) as CampaignView);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  const replace = (c: CampaignView) => setCampaigns((all) => all.map((x) => (x.id === c.id ? c : x)));
  const days = reminders
    .split(/[\s,،]+/)
    .filter(Boolean)
    .map(Number);
  return (
    <div className="policies-page">
      {canLaunch ? (
        <form
          className="policies-card"
          aria-labelledby={`${ids}-launch`}
          noValidate
          onSubmit={(e) => {
            e.preventDefault();
            void call(
              "launch",
              base,
              {
                deadline: deadline || null,
                reminderDays: reminders.trim() ? days : null,
                lateJoinerDays: lateDays.trim() ? Number(lateDays) : null,
              },
              (c) => {
                setCampaigns((all) => [c, ...all]);
                setStatus(t("launched", { count: c.counts.members }));
              },
            );
          }}
        >
          <h2 id={`${ids}-launch`}>{t("launchTitle")}</h2>
          <div className="field">
            <label htmlFor={`${ids}-deadline`}>{t("deadline")}</label>
            <input id={`${ids}-deadline`} type="date" value={deadline} onChange={(e) => setDeadline(e.target.value)} />
          </div>
          <div className="field">
            <label htmlFor={`${ids}-reminders`}>{t("reminders")}</label>
            <input id={`${ids}-reminders`} inputMode="numeric" value={reminders} onChange={(e) => setReminders(e.target.value)} dir="ltr" placeholder="7, 2" />
          </div>
          <div className="field">
            <label htmlFor={`${ids}-late`}>{t("lateDays")}</label>
            <input id={`${ids}-late`} type="number" min={1} max={90} value={lateDays} onChange={(e) => setLateDays(e.target.value)} dir="ltr" placeholder="14" />
          </div>
          <p className="policies-meta">{t("defaults")}</p>
          <div className="button-row">
            <Button type="submit" variant="primary" busy={busy === "launch"} busyLabel={t("busy")}>
              {t("launch")}
            </Button>
          </div>
        </form>
      ) : null}
      <div aria-live="polite">
        {messages.length > 0 ? (
          <div className="notice notice-error" role="alert">
            {messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
        {status ? (
          <p className="notice" role="status">
            {status}
          </p>
        ) : null}
      </div>
      {campaigns.length === 0 ? (
        <p className="policies-meta">{t("none")}</p>
      ) : (
        <ul className="policies-list" data-testid="campaign-list">
          {campaigns.map((c) => (
            <li key={c.id} className="policies-card" aria-current={focus === c.id ? "true" : undefined}>
              <h2>{t("campaign", { number: c.version, date: format.dateTime(new Date(c.launchedAt), { dateStyle: "medium" }) })}</h2>
              <p className="policies-meta">
                {t(c.status === "open" ? "openUntil" : "closedOn", {
                  date: format.dateTime(new Date(c.status === "open" ? c.deadline : (c.closedAt ?? c.deadline)), { dateStyle: "long" }),
                })}
              </p>
              <p data-testid="campaign-counts">
                {t("counts", { members: c.counts.members, attested: c.counts.attested, open: c.counts.open, overdue: c.counts.overdue })}
              </p>
              <div className="button-row">
                <Link href={`${complianceHref(policyId)}?campaign=${encodeURIComponent(c.id)}`}>{t("compliance")}</Link>
                {c.status === "open" && canLaunch ? (
                  <>
                    <Button
                      busy={busy === `remind-${c.id}`}
                      busyLabel={t("busy")}
                      onClick={() => void call(`remind-${c.id}`, `${base}/${encodeURIComponent(c.id)}/remind`, {}, (v) => {
                        replace(v);
                        setStatus(t("reminded", { count: v.counts.open + v.counts.overdue }));
                      })}
                    >
                      {t("remind")}
                    </Button>
                    <Button
                      variant="destructive"
                      busy={busy === `close-${c.id}`}
                      busyLabel={t("busy")}
                      onClick={() => void call(`close-${c.id}`, `${base}/${encodeURIComponent(c.id)}/close`, {}, (v) => {
                        replace(v);
                        setStatus(t("closed"));
                      })}
                    >
                      {t("close")}
                    </Button>
                  </>
                ) : null}
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
