"use client";

import { useId, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { nameIn, type AttestationView, type MyAttestation } from "@/lib/policies/types";
import { Button } from "@/components/ui/Button";
import "./policies.css";

/**
 * The attest panel on a policy page (spec 025 US2 scenario 2): the attestation text the person confirms, in the
 * language they read the policy in, and once given, the version and time on record. Attestation is the person's own;
 * the API refuses it for anyone else.
 */
export function AttestPanel({ policyId, initial, locale }: { policyId: string; initial: MyAttestation; locale: string }) {
  const t = useTranslations("policies.attest");
  const format = useFormatter();
  const ids = useId();
  const [state, setState] = useState(initial);
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [done, setDone] = useState<AttestationView | null>(null);

  async function attest(e: React.FormEvent) {
    e.preventDefault();
    if (!state.due) return;
    if (!confirmed) {
      setMessages([t("confirmFirst")]);
      return;
    }
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(policyId)}/attest`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ campaignId: state.due.campaignId, locale, channel: "web" }),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      const record = (await response.json()) as AttestationView;
      setDone(record);
      setState({ due: null, lastVersion: record.version, lastAt: record.attestedAt });
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  if (!state.due) {
    return state.lastVersion ? (
      <section className="policies-card" aria-labelledby={`${ids}-title`} data-testid="attest-panel">
        <h2 id={`${ids}-title`}>{t("title")}</h2>
        <p role={done ? "status" : undefined}>
          {t("given", {
            number: state.lastVersion,
            date: format.dateTime(new Date(state.lastAt ?? ""), { dateStyle: "long", timeStyle: "short" }),
          })}
        </p>
      </section>
    ) : null;
  }

  const due = state.due;
  return (
    <form className="policies-card" aria-labelledby={`${ids}-title`} onSubmit={(e) => void attest(e)} noValidate data-testid="attest-panel">
      <h2 id={`${ids}-title`}>{t("title")}</h2>
      <p className={due.overdue ? "field-error" : "policies-meta"}>
        {t(due.overdue ? "overdue" : "due", { number: due.version, date: format.dateTime(new Date(due.dueAt), { dateStyle: "long" }) })}
      </p>
      <blockquote>
        <bdi>{nameIn(due.text, locale)}</bdi>
      </blockquote>
      <label className="policies-check">
        <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
        {t("confirm")}
      </label>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <div className="button-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
          {t("submit")}
        </Button>
      </div>
    </form>
  );
}
