"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { scopeLabelKey, type ConnectedApp } from "@/lib/api-access";
import "./api-access.css";

/**
 * The apps the person allowed to use their account (spec 035 FR-029), with what each may do, when they allowed it and
 * when it last acted. Removing an app ends its access at once.
 */
export function ConnectedApps({ initial }: { initial: ConnectedApp[] }) {
  const t = useTranslations("apiAccess.connected");
  const describe = useTranslations("apiAccess.scope");
  const format = useFormatter();
  const [apps, setApps] = useState(initial);
  const [failed, setFailed] = useState(false);
  const date = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium" });

  async function remove(app: ConnectedApp) {
    setFailed(false);
    const response = await fetch(`/api/v1/me/connected-apps/${app.consentId}`, { method: "DELETE" }).catch(() => null);
    if (response?.ok) setApps((list) => list.filter((a) => a.consentId !== app.consentId));
    else setFailed(true);
  }

  return (
    <div className="connected-apps">
      {failed ? (
        <Alert tone="danger" title={t("failedTitle")} live>
          {t("failed")}
        </Alert>
      ) : null}
      {apps.length === 0 ? (
        <p className="field-hint">{t("none")}</p>
      ) : (
        <ul className="connected-apps-list">
          {apps.map((app) => (
            <li key={app.consentId}>
              <article className="connected-app" aria-labelledby={`connected-${app.consentId}`}>
                <h2 id={`connected-${app.consentId}`}>{app.name}</h2>
                <p className="field-hint">{app.publisher ? t("publisher", { publisher: app.publisher }) : t("noPublisher")}</p>
                <ul className="connected-app-scopes">
                  {app.scopes.map((s) => (
                    <li key={s}>{describe(scopeLabelKey(s))}</li>
                  ))}
                </ul>
                <p className="field-hint">
                  {t("allowed", { date: date(app.grantedAt) })}
                  {" · "}
                  {app.lastUsedAt ? t("lastUsed", { date: date(app.lastUsedAt) }) : t("neverUsed")}
                </p>
                <ConfirmAction
                  label={t("remove")}
                  question={t("removeQuestion", { app: app.name })}
                  confirmLabel={t("remove")}
                  cancelLabel={t("cancel")}
                  onConfirm={() => remove(app)}
                />
              </article>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
