"use client";

import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FactList } from "@/components/ui/Patterns";
import { problemMessages } from "@/lib/notifications/types";
import { AUTONOMY_ROLES, type ProcessAutonomy } from "@/lib/processes";
import { stepUpIfAsked } from "@/lib/security/types";
import { pick } from "@/lib/services/types";
import "./processes.css";

/**
 * Approval of a published version for autonomous agent steps (spec 027 US1 scenario 5, FR-024). Agent tasks run only
 * once a process owner and an AI policy administrator, two different people, have both approved this version. Which
 * parts the person may sign is the API's answer.
 */
export function AutonomyPanel({ initial }: { initial: ProcessAutonomy }) {
  const t = useTranslations("processes.autonomy");
  const locale = useLocale();
  const format = useFormatter();
  const [view, setView] = useState(initial);
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const when = (value: string) => format.dateTime(new Date(value), { dateStyle: "medium", timeStyle: "short" });

  async function approve(role: (typeof AUTONOMY_ROLES)[number]) {
    setBusy(role);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/workflow/processes/${encodeURIComponent(view.key)}/versions/${view.version}/autonomy:approve`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ as: role }),
      });
      if (await stepUpIfAsked(response, `/admin/processes/${encodeURIComponent(view.key)}`)) return;
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return;
      }
      setView((await response.json()) as ProcessAutonomy);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  const signed = (at: string | null) => (at ? t("signedAt", { when: when(at) }) : t("notSigned"));
  return (
    <section className="processes-section" aria-labelledby="process-autonomy">
      <h2 id="process-autonomy">{t("title", { version: String(view.version) })}</h2>
      <p>{t("intro")}</p>
      <ul className="processes-autonomy-tasks">
        {view.tasks.map((task) => (
          <li key={task.elementId}>
            <bdi>{pick(task.names, locale, task.elementId)}</bdi>
            <span>{t("taskFacts", { tools: task.tools.join(", "), minutes: String(task.maxMinutes) })}</span>
          </li>
        ))}
      </ul>
      <FactList
        facts={[
          { key: "state", label: t("state"), value: <Badge tone={view.approved ? "success" : "warning"}>{view.approved ? t("approved") : t("waiting")}</Badge> },
          { key: "owner", label: t("owner"), value: signed(view.ownerAt) },
          { key: "policy", label: t("policy"), value: signed(view.policyAt) },
        ]}
      />
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {view.canApproveAs.length > 0 ? (
        <div className="processes-autonomy-actions">
          {view.canApproveAs.map((role) => (
            <Button key={role} type="button" variant="primary" busy={busy === role} busyLabel={t("working")} onClick={() => void approve(role)}>
              {t(`approveAs.${role}`)}
            </Button>
          ))}
        </div>
      ) : null}
      <p className="field-hint">{t("note")}</p>
    </section>
  );
}
