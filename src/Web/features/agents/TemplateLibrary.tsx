"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import type { OrgUnit } from "@/lib/experience/builder-types";
import { pickText, type AgentDetail, type TemplateView } from "@/lib/agents/types";

/**
 * The launch template library (spec 020 US1 scenario 1): each template with what it does, its autonomy level and
 * whether it is ready. Installing creates a draft agent in the chosen org unit; the API checks the unit is one the
 * designer designs agents for.
 */
export function TemplateLibrary({ templates, units }: { templates: TemplateView[]; units: OrgUnit[] }) {
  const t = useTranslations("agents.templates");
  const locale = useLocale();
  const router = useRouter();
  const unitId = useId();
  const [unit, setUnit] = useState(units[0]?.id ?? "");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function install(template: TemplateView) {
    setBusy(template.id);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/agents", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ templateId: template.id, orgUnitId: unit }),
      });
      if (response.ok) {
        const detail = (await response.json()) as AgentDetail;
        router.push(`/admin/agents/${detail.agent.id}`);
        return;
      }
      setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    }
    setBusy(null);
  }

  return (
    <section className="agents-section" aria-labelledby="agents-templates">
      <h2 id="agents-templates">{t("title")}</h2>
      <p>{t("intro")}</p>
      <div className="field">
        <label htmlFor={unitId}>{t("unit")}</label>
        <select id={unitId} value={unit} onChange={(e) => setUnit(e.target.value)}>
          {units.map((u) => (
            <option key={u.id} value={u.id}>
              {pickText(u.names, locale)}
            </option>
          ))}
        </select>
      </div>
      <ul className="agents-grid" data-testid="agents-templates">
        {templates.map((template) => {
          const name = pickText(template.name, locale);
          return (
            <li key={template.id} className="agents-section">
              <h3>
                <bdi>{name}</bdi>
              </h3>
              <p>{pickText(template.description, locale)}</p>
              <p>
                <span className="agents-badge">{t("autonomy", { level: template.autonomy ?? "" })}</span>{" "}
                {template.ready ? null : <span className="agents-badge">{t("notReady")}</span>}
              </p>
              {template.ready ? null : <p>{t(template.autonomyAbovePolicy ? "notReadyPolicy" : "notReadyTools")}</p>}
              <div className="agents-row">
                <Button
                  variant="primary"
                  disabled={!template.ready || unit === ""}
                  busy={busy === template.id}
                  busyLabel={t("installing")}
                  onClick={() => void install(template)}
                  aria-label={t("installNamed", { name })}
                >
                  {t("install")}
                </Button>
              </div>
            </li>
          );
        })}
      </ul>
      {messages.length > 0 ? (
        <div role="alert" className="notice notice-error">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </section>
  );
}
