"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import type { ExternalAction } from "@/lib/agents/external";
import "./agents.css";

/** Shows a parameter value as plain text: text as it is, anything else as compact JSON. */
function show(value: unknown): string {
  return typeof value === "string" ? value : JSON.stringify(value);
}

/**
 * The card on which the person confirms or rejects what an external AI client asked to do for them (spec 035 US3
 * scenario 5, FR-022). It names the client, marks the request as coming from an external AI client, and shows every
 * parameter exactly as sent, since values may come from content the client read. Confirming sends the hash of what the
 * card shows, so the API runs exactly that and nothing else.
 */
export function ExternalActionCard({ initial }: { initial: ExternalAction }) {
  const t = useTranslations("aiActions");
  const format = useFormatter();
  const [action, setAction] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);
  const tool = t.has(`tools.${action.tool}`) ? t(`tools.${action.tool}`) : action.tool;
  const time = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" });

  async function decide(confirm: boolean) {
    setBusy(true);
    setErrors([]);
    try {
      const response = await fetch(`/api/v1/me/ai-actions/${action.id}/decision`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ confirm, hash: action.hash }),
      });
      if (response.ok) {
        setAction((await response.json()) as ExternalAction);
      } else {
        setErrors(await problemMessages(response));
        const latest = await fetch(`/api/v1/me/ai-actions/${action.id}`).catch(() => null);
        if (latest?.ok) setAction((await latest.json()) as ExternalAction);
      }
    } catch {
      setErrors([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="agents-card" aria-labelledby="ai-action-title" data-testid="ai-action">
      <h2 id="ai-action-title">{tool}</h2>
      <p>
        <span className="agents-badge">{t("external")}</span> {t("intro", { app: action.appName })}
      </p>
      <dl>
        <div className="agents-card-row">
          <dt>{t("app")}</dt>
          <dd>
            <bdi>{action.appName}</bdi>
          </dd>
        </div>
        {Object.entries(action.parameters).map(([key, value]) => (
          <div key={key} className="agents-card-row">
            <dt>
              <bdi>{key}</bdi>
            </dt>
            <dd>
              <bdi>{show(value)}</bdi>
            </dd>
          </div>
        ))}
      </dl>
      <p className="field-hint">{t("check")}</p>
      {errors.length > 0 ? (
        <Alert tone="danger" title={t("failedTitle")} live>
          {errors.join(" ")}
        </Alert>
      ) : null}
      {action.status === "pending" ? (
        <>
          <p className="field-hint">{t("expires", { time: time(action.expiresAt) })}</p>
          <div className="agents-row">
            <Button variant="primary" busy={busy} busyLabel={t("working")} onClick={() => void decide(true)}>
              {t("confirm")}
            </Button>
            <Button busy={busy} busyLabel={t("working")} onClick={() => void decide(false)}>
              {t("reject")}
            </Button>
          </div>
        </>
      ) : (
        <Alert tone={action.status === "confirmed" ? "success" : "info"} title={t(`status.${action.status}`)} live>
          {action.decidedAt ? t("decidedAt", { time: time(action.decidedAt) }) : null}
        </Alert>
      )}
    </section>
  );
}
