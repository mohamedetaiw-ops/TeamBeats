"use client";

import Link from "next/link";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Icon, type IconName } from "@/components/ui/Icon";
import { problemMessages } from "@/lib/notifications/types";
import { pick, type ServiceTemplateView } from "@/lib/services/types";
import "./services.css";

/**
 * The starter template pack (spec 016 section 12): a tenant-wide catalogue owner switches a template on, which makes
 * a draft service to edit and publish. A template that is on links to its service; one outside the plan says so.
 */
export function ServiceTemplateList({ initial }: { initial: ServiceTemplateView[] }) {
  const t = useTranslations("services.templates");
  const ts = useTranslations("services.admin");
  const locale = useLocale();
  const router = useRouter();
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function switchOn(key: string) {
    setBusy(key);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/services/admin/templates/${encodeURIComponent(key)}`, { method: "POST" });
      if (response.ok) {
        const view = (await response.json()) as { service: { id: string } };
        router.push(`/admin/services/${view.service.id}`);
        return;
      }
      setMessages(await problemMessages(response));
    } catch {
      setMessages([ts("failed")]);
    } finally {
      setBusy(null);
    }
  }

  return (
    <section className="services-section" aria-labelledby="service-templates">
      <h2 id="service-templates">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <ul className="services-template-list">
        {initial.map((tpl) => {
          const name = pick(tpl.names, locale);
          return (
            <li key={tpl.key} className="services-template" data-testid="service-template">
              <Icon name={(tpl.icon ?? "services") as IconName} size={20} />
              <div>
                <h3>
                  <bdi>{name}</bdi>
                </h3>
                <p className="field-hint">{pick(tpl.descriptions, locale)}</p>
              </div>
              <div className="services-actions">
                {tpl.serviceId ? (
                  <>
                    <Badge tone={tpl.serviceStatus === "published" ? "success" : "neutral"}>{ts(`state.${tpl.serviceStatus ?? "draft"}`)}</Badge>
                    <Link href={`/admin/services/${tpl.serviceId}`}>{t("open", { name })}</Link>
                  </>
                ) : tpl.available ? (
                  <Button busy={busy === tpl.key} busyLabel={t("switchingOn")} onClick={() => void switchOn(tpl.key)} aria-label={t("switchOnNamed", { name })}>
                    {t("switchOn")}
                  </Button>
                ) : (
                  <Badge tone="neutral">{t("notInPlan")}</Badge>
                )}
              </div>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
