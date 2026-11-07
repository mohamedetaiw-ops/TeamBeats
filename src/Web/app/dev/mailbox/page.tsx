import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import "@/features/notifications/notifications.css";

type CapturedMail = { id: string; to: string; fromName: string; subject: string; text: string; capturedAt: string };

/**
 * Development and staging mailbox (spec 015 T008): email the platform would have sent, captured instead of delivered.
 * It exists only where the development fixtures are on; the API serves it to notification administrators only.
 */
export default async function DevMailbox() {
  if (process.env.NEXT_PUBLIC_TB_REFERENCE_PACKS !== "true") {
    notFound();
  }
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [mail, t, locale] = await Promise.all([apiGet<CapturedMail[]>("/api/v1/dev/notifications/mailbox"), getTranslations("notifications.mailbox"), getLocale()]);
  if (!mail.ok) {
    notFound();
  }
  const when = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" });
  return (
    <>
      <Header me={me.data} />
      <main id="main" className="page">
        <h1>{t("title")}</h1>
        <p className="notice">{t("intro")}</p>
        {mail.data.length === 0 ? (
          <StateView kind="empty" title={t("empty")} message="" />
        ) : (
          <ul className="notification-list notification-list-full">
            {mail.data.map((m) => (
              <li key={m.id} className="notification-mail">
                <h2 className="notification-panel-title">
                  <bdi>{m.subject}</bdi>
                </h2>
                <p className="notification-entry-meta">
                  <span>
                    {t("to")} <bdi>{m.to}</bdi>
                  </span>
                  <span>
                    {t("from")} <bdi>{m.fromName}</bdi>
                  </span>
                  <time dateTime={m.capturedAt}>{when.format(new Date(m.capturedAt))}</time>
                </p>
                <pre className="notification-mail-text" dir="auto">
                  {m.text}
                </pre>
              </li>
            ))}
          </ul>
        )}
      </main>
    </>
  );
}
