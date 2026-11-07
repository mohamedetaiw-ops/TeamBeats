"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { connectHref, useOutlookData, useReturnPath, type OutlookState } from "@/components/experience/outlook";
import { Button } from "@/components/ui/Button";

type Status = { state: OutlookState; features: string[]; connectedAt: string | null; expiresAt: string | null };

/**
 * The employee's own Outlook connection in personal settings (spec 018 FR-013): connect through the Microsoft sign-in
 * page, or disconnect, which deletes TeamBeats' delegated token. The section is left out when the tenant's plan or
 * connection has no mail or calendar: the server answers that, the client does not decide it.
 */
export function OutlookSettings() {
  const t = useTranslations("outlook.settings");
  const returnTo = useReturnPath();
  const { answer, retry } = useOutlookData<Status>("/api/v1/me/outlook", false);
  const [busy, setBusy] = useState(false);

  if (answer === "loading" || answer === "denied" || answer === "failed") return null;
  if (answer.state === "turned_off" || answer.state === "not_in_plan") return null;

  async function disconnect() {
    setBusy(true);
    try {
      await fetch("/api/v1/me/outlook", { method: "DELETE" });
      retry();
    } finally {
      setBusy(false);
    }
  }

  const connect = (label: string) => (
    <a className="button button-secondary" href={connectHref(returnTo)}>
      {label}
    </a>
  );
  let body;
  switch (answer.state) {
    case "ok":
      body = (
        <>
          <p>{t("connected")}</p>
          <Button variant="secondary" busy={busy} busyLabel={t("disconnecting")} onClick={() => void disconnect()}>
            {t("disconnect")}
          </Button>
        </>
      );
      break;
    case "reconnect":
      body = (
        <>
          <p>{t("reconnect")}</p>
          {connect(t("reconnectAction"))}
        </>
      );
      break;
    case "admin_setup":
      body = <p>{t("adminSetup")}</p>;
      break;
    case "conditional_access":
      body = <p>{t("conditionalAccess")}</p>;
      break;
    case "degraded":
      body = <p>{t("unavailable")}</p>;
      break;
    default:
      body = (
        <>
          <p>{t("notConnected")}</p>
          {connect(t("connect"))}
        </>
      );
  }
  return (
    <section className="outlook-settings" aria-label={t("title")}>
      <h2 className="outlook-settings-title">{t("title")}</h2>
      <div role="status" className="outlook-settings-body">
        {body}
      </div>
    </section>
  );
}
