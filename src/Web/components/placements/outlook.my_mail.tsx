"use client";

import { useId, useState, type ReactNode } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";
import { OutlookStateView, StaleNote, useHydrated, useOutlookData, useReturnPath, type OutlookState } from "@/components/experience/outlook";
import { StateView } from "@/components/ui/StateView";
import { Badge } from "@/components/ui/Badge";
import { SegmentedControl } from "@/components/ui/SegmentedControl";

type Message = {
  id: string;
  sender: string;
  subject: string;
  receivedAt: string;
  isRead: boolean;
  importance: string;
  hasAttachments: boolean;
  preview: string | null;
  webLink: string;
};
type Summary = { state: OutlookState; asOf: string | null; unreadCount: number | null; messages: Message[]; composeLink: string | null };

/**
 * My mail (spec 018 US2): the Inbox unread count and the latest messages with text markers, each opening in Outlook on
 * the web. TeamBeats never renders a message body; the preview is plain text when the admin turned it on (FR-008).
 */
export default function MyMail({ props }: PlacementProps) {
  const t = useTranslations("outlook.mail");
  const locale = useLocale();
  const returnTo = useReturnPath();
  const hydrated = useHydrated();
  const group = useId();
  const [view, setView] = useState<"focused" | "all">(props.view === "all" ? "all" : "focused");
  const top = typeof props.top === "number" && props.top >= 1 && props.top <= 10 ? Math.floor(props.top) : 5;
  const { answer, retry } = useOutlookData<Summary>(`/api/v1/me/mail?view=${view}&top=${top}`);

  const unread = answer !== "loading" && answer !== "failed" && answer !== "denied" ? answer.unreadCount : null;
  const shell = (body: ReactNode, busy = false) => (
    <section className="outlook" aria-label={t("title")} aria-busy={busy || undefined}>
      <div className="outlook-header">
        <h2 className="outlook-title">{t("title")}</h2>
        {unread !== null && unread > 0 ? <Badge tone="accent">{t("unread", { count: unread })}</Badge> : null}
      </div>
      {body}
    </section>
  );

  if (answer === "loading" || !hydrated) return shell(<StateView kind="loading" minBlockSize="10rem" />, true);
  const state = answer === "failed" || answer === "denied" ? answer : answer.state;
  const other = <OutlookStateView state={state} returnTo={returnTo} retry={retry} noun="mail" />;
  if (answer === "failed" || answer === "denied" || (state !== "ok" && state !== "degraded")) return shell(other);

  const time = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" });
  const switcher = (
    <SegmentedControl
      label={t("view")}
      name={`mail-view-${group}`}
      value={view}
      options={(["focused", "all"] as const).map((v) => ({ value: v, label: t(`views.${v}`) }))}
      onChange={(v) => setView(v === "all" ? "all" : "focused")}
    />
  );
  const compose = answer.composeLink ? (
    <a className="button button-secondary" href={answer.composeLink} target="_blank" rel="noopener noreferrer">
      {t("compose")}
      <span className="visually-hidden"> {t("opensOutlook")}</span>
    </a>
  ) : null;

  return shell(
    <>
      <div className="outlook-actions">
        {switcher}
        {compose}
      </div>
      {answer.state === "degraded" ? <StaleNote asOf={answer.asOf} locale={locale} retry={retry} /> : null}
      {answer.messages.length === 0 ? (
        <StateView kind="empty" title={t("empty")} message="" />
      ) : (
        <ul className="outlook-list">
          {answer.messages.map((m) => {
            const markers = [
              m.isRead ? null : t("markers.unread"),
              m.importance === "high" ? t("markers.important") : null,
              m.hasAttachments ? t("markers.attachment") : null,
            ].filter(Boolean);
            return (
              <li key={m.id} className="outlook-item" data-unread={m.isRead ? undefined : "true"}>
                <a className="outlook-link" href={m.webLink} target="_blank" rel="noopener noreferrer">
                  <span className="outlook-line">
                    <bdi className="outlook-sender">{m.sender}</bdi>
                    <time className="outlook-time" dateTime={m.receivedAt}>
                      {time.format(new Date(m.receivedAt))}
                    </time>
                  </span>
                  <bdi className="outlook-subject">{m.subject || t("noSubject")}</bdi>
                  {markers.length > 0 ? (
                    <span className="outlook-markers">
                      {markers.map((marker, i) => (
                        <Badge key={marker} tone={i === 0 && !m.isRead ? "accent" : "neutral"}>
                          {marker}
                        </Badge>
                      ))}
                    </span>
                  ) : null}
                  {m.preview ? <span className="outlook-preview">{m.preview}</span> : null}
                  <span className="visually-hidden">{t("opensOutlook")}</span>
                </a>
              </li>
            );
          })}
        </ul>
      )}
    </>,
  );
}
