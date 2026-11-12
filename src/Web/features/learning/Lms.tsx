"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { call, learningApi, send, type LmsStatus } from "@/lib/learning";
import "./learning.css";

/**
 * The LMS sync for learning administrators (spec 028 US6): whether the LMS answers, when its catalogue and completions
 * were last read, how many courses came from it and how many assignments wait to be sent, and a button to sync now.
 * Without an LMS connection it points to Connections, where an administrator adds one.
 */
export function LmsPanel({ status }: { status: LmsStatus }) {
  const t = useTranslations("learning.lms");
  const format = useFormatter();
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const when = (at: string | null) => (at ? format.dateTime(new Date(at), { dateStyle: "medium", timeStyle: "short" }) : t("never"));

  async function sync() {
    setBusy(true);
    setMessages([]);
    const result = await call<LmsStatus>(() => send(`${learningApi}/manage/lms:sync`, "POST"), t("failed"));
    setBusy(false);
    if (result.ok) router.refresh();
    else setMessages(result.messages);
  }

  return (
    <section className="learning-section" aria-labelledby="manage-lms">
      <h2 id="manage-lms">{t("title")}</h2>
      {!status.configured ? (
        <p className="learning-meta">
          {t("notConfigured")} <Link href="/admin/connections">{t("connections")}</Link>
        </p>
      ) : (
        <>
          <div className="learning-row">
            {status.available ? (
              <Badge tone="success" icon="check">
                {t("available")}
              </Badge>
            ) : (
              <Badge tone="warning" icon="alert">
                {t("unavailable")}
              </Badge>
            )}
            <Badge icon="book">{t("courses", { count: status.courses })}</Badge>
            {status.waiting > 0 ? <Badge tone="info">{t("waiting", { count: status.waiting })}</Badge> : null}
            {status.refused > 0 ? <Badge tone="danger">{t("refused", { count: status.refused })}</Badge> : null}
          </div>
          <p className="learning-meta">{t("lastRead", { catalogue: when(status.catalogueAt), completions: when(status.completionsAt) })}</p>
          <div className="learning-row">
            <Button icon="download" busy={busy} onClick={() => void sync()}>
              {t("syncNow")}
            </Button>
          </div>
        </>
      )}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
    </section>
  );
}
