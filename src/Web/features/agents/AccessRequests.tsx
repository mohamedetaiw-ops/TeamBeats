"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import { problemMessages } from "@/lib/notifications/types";
import type { AccessRequestView } from "@/lib/agents/types";

/**
 * Requests to use an agent from people outside its audience (spec 020 US6 scenario 2), for its owners. The API lists
 * them only to an owner and decides each grant or denial; the requester is told either way.
 */
export function AccessRequests({
  requests,
}: {
  requests: AccessRequestView[];
}) {
  const t = useTranslations("agents.access");
  const format = useFormatter();
  const router = useRouter();
  const ids = useId();
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function decide(id: string, grant: boolean) {
    setBusy(id);
    setMessages([]);
    try {
      const response = await fetch(
        `/api/v1/agents/access-requests/${id}/decision`,
        {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ grant }),
        },
      );
      if (response.ok) {
        router.refresh();
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    }
    setBusy(null);
  }

  return (
    <section
      id="access"
      className="agents-section"
      aria-labelledby={`${ids}-title`}
      data-testid="agent-access"
    >
      <h2 id={`${ids}-title`}>{t("title")}</h2>
      <p>{t("intro")}</p>
      {requests.length === 0 ? (
        <StateView kind="empty" title={t("none")} message="" />
      ) : (
        <ul className="agents-access-list">
          {requests.map((r) => (
            <li key={r.id}>
              <span>
                <bdi>{r.userName ?? t("unknown")}</bdi>
                {" · "}
                <time dateTime={r.createdAt}>
                  {format.dateTime(new Date(r.createdAt), {
                    dateStyle: "medium",
                  })}
                </time>
              </span>
              <span className="agents-market-actions">
                <Button
                  size="sm"
                  disabled={busy !== null}
                  onClick={() => decide(r.id, true)}
                >
                  {t("grant")}
                </Button>
                <Button
                  size="sm"
                  variant="secondary"
                  disabled={busy !== null}
                  onClick={() => decide(r.id, false)}
                >
                  {t("deny")}
                </Button>
              </span>
            </li>
          ))}
        </ul>
      )}
      {messages.map((m) => (
        <p key={m} role="alert" className="field-error">
          {m}
        </p>
      ))}
    </section>
  );
}
