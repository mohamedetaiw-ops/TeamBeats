"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { send } from "@/lib/evaluation/send";

/** Starts an evaluation run of one version (spec 034 US3 scenario 7) and opens it; the API refuses what the person may not run. */
export function StartRun({ agentId, versions, latest }: { agentId: string; versions: number[]; latest: number }) {
  const t = useTranslations("evaluation");
  const router = useRouter();
  const id = useId();
  const [version, setVersion] = useState(latest);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function start() {
    setBusy(true);
    setMessages([]);
    const result = await send<{ run: { id: string } }>("/api/v1/evaluation/runs", "POST", { agentId, version }, t("failed"));
    setBusy(false);
    if (result.ok) router.push(`/admin/evaluation/runs/${result.data.run.id}`);
    else setMessages(result.messages);
  }

  return (
    <div className="agents-row">
      <div className="field">
        <label htmlFor={`${id}-version`}>{t("start.version")}</label>
        <select id={`${id}-version`} value={version} onChange={(e) => setVersion(Number(e.target.value))}>
          {versions.map((v) => (
            <option key={v} value={v}>
              {t("dashboard.versionNumber", { number: String(v) })}
            </option>
          ))}
        </select>
      </div>
      <Button variant="primary" busy={busy} busyLabel={t("working")} onClick={() => void start()} data-testid="eval-start">
        {t("start.button")}
      </Button>
      {messages.length > 0 ? (
        <div role="alert" className="notice notice-error">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
    </div>
  );
}
