"use client";

import { useEffect, useState, type ReactNode } from "react";
import { useTranslations } from "next-intl";
import { connectHref, useOutlookData, useReturnPath, type OutlookState } from "@/components/experience/outlook";
import { Button } from "@/components/ui/Button";
import { Switch } from "@/components/ui/Switch";

type Status = { state: OutlookState; features: string[]; connectedAt: string | null; expiresAt: string | null };

/**
 * The employee's own Outlook connection in personal settings (spec 018 FR-013): connect through the Microsoft sign-in
 * page, or disconnect, which deletes TeamBeats' delegated token. The section is left out when the tenant's plan or
 * connection has no mail or calendar: the server answers that, the client does not decide it.
 */
export function OutlookSettings({ unavailable = null }: { unavailable?: ReactNode } = {}) {
  const t = useTranslations("outlook.settings");
  const returnTo = useReturnPath();
  const { answer, retry } = useOutlookData<Status>("/api/v1/me/outlook", false);
  const [busy, setBusy] = useState(false);

  if (answer === "loading") return null;
  if (answer === "denied" || answer === "failed") return unavailable;
  if (answer.state === "turned_off" || answer.state === "not_in_plan") return unavailable;

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
          <MailSummaries />
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

type SummaryStatus = { offered: boolean; optedIn: boolean };

/**
 * The employee's own opt-in to mail summaries by the assistant (spec 018 R3, FR-022). Shown only while the tenant offers
 * them, which the server answers; the opt-in is saved at once and can be taken back at any time.
 */
function MailSummaries() {
  const t = useTranslations("outlook.settings");
  const [status, setStatus] = useState<SummaryStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let live = true;
    fetch("/api/v1/me/outlook/mail-summaries", { cache: "no-store" })
      .then(async (r) => (r.ok ? ((await r.json()) as SummaryStatus) : null))
      .catch(() => null)
      .then((value) => {
        if (live) setStatus(value);
      });
    return () => {
      live = false;
    };
  }, []);
  if (!status || (!status.offered && !status.optedIn)) return null;

  async function choose(optedIn: boolean) {
    const before = status!;
    // The switch moves at once; a refused save puts it back and says so.
    setStatus({ ...before, optedIn });
    setBusy(true);
    setFailed(false);
    try {
      const response = await fetch("/api/v1/me/outlook/mail-summaries", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ optedIn }),
      });
      if (response.ok) setStatus((await response.json()) as SummaryStatus);
      else {
        setStatus(before);
        setFailed(true);
      }
    } catch {
      setStatus(before);
      setFailed(true);
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <Switch
        label={t("mailSummaries")}
        hint={t("mailSummariesHint")}
        checked={status.optedIn}
        disabled={busy}
        onChange={(e) => void choose(e.target.checked)}
        data-testid="outlook-mail-summaries"
      />
      {failed ? <p role="alert">{t("mailSummariesFailed")}</p> : null}
    </>
  );
}
