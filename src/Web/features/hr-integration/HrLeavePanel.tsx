"use client";

import Link from "next/link";
import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { HR_LEAVE_HANDOFF_KINDS, HR_LEAVE_HANDOFF_STATES, HR_LEAVE_SOURCES, known, type HrLeaveHandOff, type HrLeaveSetting } from "@/lib/integrations/types";
import { problemMessages } from "@/lib/notifications/types";
import "./hr-integration.css";

const BASE = "/api/v1/hr/integration";

type Result = { ok: boolean; messages: string[] } | null;

/**
 * Leave on the HR integration page (spec 019 US7, FR-013; spec 022 FR-006): whether TeamBeats or HR keeps leave
 * balances, whether a request may go in on the last known balance while HR is down, and the leave handed to HR with
 * its state, where a failed one is sent again. The API checks the permission, the plan and the version.
 */
export function HrLeavePanel({ initialSetting, initialHandOffs }: { initialSetting: HrLeaveSetting; initialHandOffs: HrLeaveHandOff[] }) {
  const t = useTranslations("hr_integration.leave");
  const format = useFormatter();
  const [setting, setSetting] = useState(initialSetting);
  const [handOffs, setHandOffs] = useState(initialHandOffs);
  const [source, setSource] = useState<string>(initialSetting.source);
  const [stale, setStale] = useState(initialSetting.allowStaleSubmit);
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<Result>(null);

  const when = (at: string) => format.dateTime(new Date(at), { dateStyle: "medium", timeStyle: "short" });

  async function act(key: string, action: () => Promise<Response>, success: string) {
    setBusy(key);
    setResult(null);
    try {
      const response = await action();
      if (response.ok) {
        const [s, h] = await Promise.all([fetch(`${BASE}/leave-source`, { cache: "no-store" }), fetch(`${BASE}/leave-handoffs`, { cache: "no-store" })]);
        if (s.ok) setSetting((await s.json()) as HrLeaveSetting);
        if (h.ok) setHandOffs((await h.json()) as HrLeaveHandOff[]);
        setResult({ ok: true, messages: [success] });
      } else {
        const messages = await problemMessages(response);
        setResult({ ok: false, messages: messages.length > 0 ? messages : [t("failed")] });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(null);
    }
  }

  const changed = source !== setting.source || stale !== setting.allowStaleSubmit;

  return (
    <section className="hr-integration-section" aria-labelledby="hr-leave-title">
      <h2 id="hr-leave-title">{t("title")}</h2>
      <p>{t("intro")}</p>
      <form
        className="hr-integration-form"
        onSubmit={(e) => {
          e.preventDefault();
          void act(
            "setting",
            () =>
              fetch(`${BASE}/leave-source`, {
                method: "PUT",
                headers: { "content-type": "application/json", "if-match": `"v${setting.version}"` },
                body: JSON.stringify({ source, allowStaleSubmit: stale }),
              }),
            t("saved"),
          );
        }}
      >
        <SegmentedControl
          label={t("source")}
          name="hr-leave-source"
          value={source}
          options={HR_LEAVE_SOURCES.map((s) => ({ value: s, label: t(`sources.${s}`) }))}
          onChange={setSource}
        />
        <Checkbox label={t("allowStale")} hint={t("allowStaleHint")} checked={stale} onChange={(e) => setStale(e.target.checked)} />
        {setting.source === "hr" && !setting.fromHr ? <p className="notice">{t("notActive")}</p> : null}
        <Button type="submit" variant="primary" disabled={!changed} busy={busy === "setting"} busyLabel={t("working")}>
          {t("save")}
        </Button>
      </form>

      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}

      <h3 id="hr-leave-handoffs-title">{t("handOffs.title")}</h3>
      {handOffs.length === 0 ? (
        <p className="notice">{t("handOffs.empty")}</p>
      ) : (
        <div className="hr-integration-table">
          <table aria-labelledby="hr-leave-handoffs-title">
            <thead>
              <tr>
                <th scope="col">{t("handOffs.request")}</th>
                <th scope="col">{t("handOffs.kind")}</th>
                <th scope="col">{t("handOffs.state")}</th>
                <th scope="col">{t("handOffs.actions")}</th>
              </tr>
            </thead>
            <tbody>
              {handOffs.map((h) => (
                <tr key={h.id}>
                  <th scope="row">
                    <Link href={`/leave/requests/${h.requestId}`}>{t("handOffs.open", { change: h.change })}</Link>
                    <span className="hr-integration-meta">{when(h.createdAt)}</span>
                  </th>
                  <td>{t(`kinds.${known(HR_LEAVE_HANDOFF_KINDS, h.kind) ? h.kind : "changed"}`)}</td>
                  <td>
                    {t(`states.${known(HR_LEAVE_HANDOFF_STATES, h.state) ? h.state : "queued"}`)}
                    <span className="hr-integration-meta">
                      {h.state === "recorded" && h.externalReference ? (
                        <bdi dir="ltr">{h.externalReference}</bdi>
                      ) : h.state === "queued" && h.nextRetryAt ? (
                        t("handOffs.nextTry", { at: when(h.nextRetryAt) })
                      ) : h.state === "failed" && h.errorClass ? (
                        <bdi dir="ltr">{h.errorClass}</bdi>
                      ) : null}
                    </span>
                  </td>
                  <td>
                    {h.state === "failed" ? (
                      <Button
                        variant="secondary"
                        busy={busy === h.id}
                        busyLabel={t("working")}
                        onClick={() => void act(h.id, () => fetch(`${BASE}/leave-handoffs/${h.id}:retry`, { method: "POST" }), t("handOffs.resent"))}
                      >
                        {t("handOffs.retry")}
                      </Button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
