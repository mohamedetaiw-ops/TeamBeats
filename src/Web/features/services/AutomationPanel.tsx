"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { TextAreaField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { AUTOMATION_STATES, pick, type RequestAutomation } from "@/lib/services/types";

const known = (s: string): s is (typeof AUTOMATION_STATES)[number] => (AUTOMATION_STATES as readonly string[]).includes(s);

/**
 * The automatic steps of a request (spec 016 US8 scenario 5): each step's state and the record the system returned.
 * A step that failed shows "automation failed"; a service owner can retry it, skip it or cancel the request, with a
 * reason. Whether the person may act comes from the API, which checks it again on every action.
 */
export function AutomationPanel({ requestId, automation }: { requestId: string; automation: RequestAutomation }) {
  const t = useTranslations("services.request.automation");
  const format = useFormatter();
  const locale = useLocale();
  const router = useRouter();
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [problem, setProblem] = useState<string[]>([]);

  async function resolve(incidentId: string, resolution: "retried" | "skipped" | "cancelled") {
    setBusy(resolution);
    setProblem([]);
    try {
      const response = await fetch(`/api/v1/requests/${requestId}/automation/incidents/${incidentId}:resolve`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ resolution, reason }),
      });
      if (response.ok) {
        setReason("");
        router.refresh();
      } else {
        setProblem(await problemMessages(response));
      }
    } finally {
      setBusy(null);
    }
  }

  return (
    <section className="services-section" aria-labelledby="request-automation">
      <h2 id="request-automation">{t("title")}</h2>
      <ol className="services-list" data-testid="request-automation">
        {automation.steps.map((s) => (
          <li key={s.elementId} className="services-item">
            <strong>
              <bdi>{pick(s.names, locale, s.name)}</bdi>
            </strong>
            <span className="services-status" data-testid="automation-status">
              {known(s.status) ? t(`states.${s.status}`) : s.status}
            </span>
            <span className="services-meta">{format.dateTime(new Date(s.startedAt), { dateStyle: "medium", timeStyle: "short" })}</span>
            {s.externalReference ? (
              <p>
                {t("reference")} <bdi dir="ltr">{s.externalReference}</bdi>
              </p>
            ) : null}
            {s.errorCode ? (
              <p className="services-note">
                {t("errorCode")} <bdi dir="ltr">{s.errorCode}</bdi>
              </p>
            ) : null}
            {s.incidentId && automation.canOperate ? (
              <div className="services-form">
                <p className="field-hint">{t("operatorHint")}</p>
                <TextAreaField label={t("reason")} name={`reason-${s.elementId}`} required maxLength={2000} value={reason} onChange={(e) => setReason(e.target.value)} />
                <div className="services-actions">
                  <Button variant="primary" busy={busy === "retried"} busyLabel={t("working")} onClick={() => void resolve(s.incidentId!, "retried")}>
                    {t("retry")}
                  </Button>
                  <Button busy={busy === "skipped"} busyLabel={t("working")} onClick={() => void resolve(s.incidentId!, "skipped")}>
                    {t("skip")}
                  </Button>
                  <Button variant="destructive" busy={busy === "cancelled"} busyLabel={t("working")} onClick={() => void resolve(s.incidentId!, "cancelled")}>
                    {t("cancel")}
                  </Button>
                </div>
              </div>
            ) : null}
          </li>
        ))}
      </ol>
      {problem.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {problem.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </section>
  );
}
