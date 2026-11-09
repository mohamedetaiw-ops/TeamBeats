"use client";

import { type ReactNode, useEffect, useState } from "react";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import "./outlook.css";

/** The states that come with every mail and calendar answer (spec 018 plan "Ports"). */
export type OutlookState =
  | "ok"
  | "not_connected"
  | "reconnect"
  | "admin_setup"
  | "no_mailbox"
  | "turned_off"
  | "conditional_access"
  | "degraded"
  | "not_in_plan";

/** Outlook on the web, for the conditional access explanation when Microsoft returned no link of its own (FR-012). */
export const OUTLOOK_WEB = "https://outlook.office.com/";

export type Fetched<T> = T | "loading" | "failed" | "denied";

/**
 * Reads one of the employee's own Outlook endpoints. Each request has a key; an answer for another key means the
 * current one is still loading. Retry asks the server to read Microsoft again (refresh=true) unless the endpoint
 * reads only TeamBeats' own records, such as the connection status.
 */
export function useOutlookData<T>(url: string, refreshable = true) {
  const [attempt, setAttempt] = useState(0);
  const [result, setResult] = useState<{ key: string; answer: T | "failed" | "denied" } | null>(null);
  const key = `${url}#${attempt}`;

  useEffect(() => {
    let live = true;
    const refresh = refreshable && attempt > 0 ? `${url.includes("?") ? "&" : "?"}refresh=true` : "";
    fetch(`${url}${refresh}`)
      .then(async (response) => {
        if (response.ok) return (await response.json()) as T;
        return response.status === 403 ? ("denied" as const) : ("failed" as const);
      })
      .catch(() => "failed" as const)
      .then((answer) => {
        if (live) setResult({ key, answer });
      });
    return () => {
      live = false;
    };
  }, [url, attempt, key, refreshable]);

  const answer: Fetched<T> = result?.key === key ? result.answer : "loading";
  return { answer, retry: () => setAttempt((n) => n + 1) };
}

/** The current path, so Connect Outlook returns the employee to the page they started from. */
export function useReturnPath(): string {
  return usePathname() || "/";
}

export function connectHref(returnTo: string): string {
  return `/api/v1/me/outlook/connect?returnTo=${encodeURIComponent(returnTo)}`;
}

export { useHydrated, useNow } from "@/lib/clock";

/**
 * The shared state view for every answer that is not data (spec 018 section 10, spec 008 FR-006). Returns null for
 * ok and degraded, which the component draws with its data.
 */
export function OutlookStateView({
  state,
  returnTo,
  retry,
  noun,
}: {
  state: OutlookState | "failed" | "denied";
  returnTo: string;
  retry: () => void;
  noun: "mail" | "calendar";
}): ReactNode {
  const t = useTranslations("outlook.state");
  const connect = (label: string) => (
    <a className="button button-primary" href={connectHref(returnTo)}>
      {label}
    </a>
  );
  switch (state) {
    case "ok":
    case "degraded":
      return null;
    case "not_connected":
      return <StateView kind="empty" title={t("notConnected")} message={t(`notConnectedMessage.${noun}`)} action={connect(t("connect"))} />;
    case "reconnect":
      return <StateView kind="empty" title={t("reconnect")} message={t("reconnectMessage")} action={connect(t("reconnectAction"))} />;
    case "admin_setup":
      return <StateView kind="unsupported" title={t("adminSetup")} message={t("adminSetupMessage")} />;
    case "no_mailbox":
      return <StateView kind="empty" title={t("noMailbox")} message="" />;
    case "turned_off":
      return <StateView kind="unsupported" title={t(`turnedOff.${noun}`)} message="" />;
    case "conditional_access":
      return (
        <StateView
          kind="denied"
          title={t("conditionalAccess")}
          message=""
          action={
            <a className="button button-secondary" href={OUTLOOK_WEB} target="_blank" rel="noopener noreferrer">
              {t("openOutlook")}
            </a>
          }
        />
      );
    case "not_in_plan":
    case "denied":
      return <StateView kind="denied" title={t("notInPlan")} message="" />;
    default:
      return (
        <StateView
          kind="error"
          title={t(`unavailable.${noun}`)}
          message=""
          action={
            <Button variant="secondary" onClick={retry}>
              {t("retry")}
            </Button>
          }
        />
      );
  }
}

/** Degraded (FR-011): the data shown is the last one read, with its age and a retry. */
export function StaleNote({ asOf, locale, retry }: { asOf: string | null; locale: string; retry: () => void }) {
  const t = useTranslations("outlook.state");
  const time = asOf ? new Intl.DateTimeFormat(locale, { timeStyle: "short" }).format(new Date(asOf)) : "";
  return (
    <div role="status" className="outlook-stale">
      <p>{asOf ? t("stale", { time }) : t("staleNoData")}</p>
      <Button variant="secondary" onClick={retry}>
        {t("retry")}
      </Button>
    </div>
  );
}
