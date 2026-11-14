"use client";

import Link from "next/link";
import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { known, USE_CASES, type DebugWindow } from "@/lib/ai/types";
import { useHydrated } from "@/lib/clock";

type Result = { ok: boolean; messages: string[] } | null;

/**
 * Debug windows (spec 013 US8, FR-010): the AI Policy Administrator opens one for a use case for 1 to 72 hours or
 * closes it early; security officers open its entries. Every rule (who, how long, one per use case, the log key) is the
 * API's; this screen sends the form and shows the answer.
 */
export function DebugWindows({ initial, canManage }: { initial: DebugWindow[]; canManage: boolean }) {
  const t = useTranslations("ai.debug");
  const tc = useTranslations("ai.routing.useCases");
  const format = useFormatter();
  const hydrated = useHydrated();
  const [windows, setWindows] = useState(initial);
  const [useCase, setUseCase] = useState<string>("assistant");
  const [hours, setHours] = useState("4");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const when = (iso: string) => (hydrated ? format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" }) : "");
  const name = (u: string) => {
    const k = known(USE_CASES, u);
    return k ? tc(k) : u;
  };

  async function call(url: string, method: string, body: unknown, done: (w: DebugWindow) => string) {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(url, { method, headers: { "content-type": "application/json" }, body: body === null ? undefined : JSON.stringify(body) });
      if (response.ok) {
        const window = (await response.json()) as DebugWindow;
        setWindows((list) => [window, ...list.filter((w) => w.id !== window.id)]);
        setResult({ ok: true, messages: [done(window)] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="ai">
      <p>{t("intro")}</p>
      {canManage ? (
        <form
          className="ai-section"
          aria-labelledby="ai-debug-open"
          onSubmit={(e) => {
            e.preventDefault();
            void call("/api/v1/ai/debug-windows", "POST", { useCase, hours: Number(hours) }, (w) => t("opened", { useCase: name(w.useCase) }));
          }}
        >
          <h2 id="ai-debug-open">{t("open")}</h2>
          <div className="ui-control-row">
            <div className="field">
              <label htmlFor="ai-debug-use-case">{t("useCase")}</label>
              <select id="ai-debug-use-case" value={useCase} onChange={(e) => setUseCase(e.target.value)}>
                {USE_CASES.filter((u) => u !== "embed").map((u) => (
                  <option key={u} value={u}>
                    {tc(u)}
                  </option>
                ))}
              </select>
            </div>
            <FormField label={t("hours")} hint={t("hoursHint")} name="hours" type="number" min={1} max={72} value={hours} onChange={(e) => setHours(e.target.value)} />
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
              {t("openButton")}
            </Button>
          </div>
        </form>
      ) : null}

      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}

      <section className="ai-section" aria-labelledby="ai-debug-list">
        <h2 id="ai-debug-list">{t("list")}</h2>
        {windows.length === 0 ? (
          <p>{t("none")}</p>
        ) : (
          <div className="ai-table">
            <table data-testid="ai-debug-windows">
              <thead>
                <tr>
                  <th scope="col">{t("useCase")}</th>
                  <th scope="col">{t("openedAt")}</th>
                  <th scope="col">{t("endsAt")}</th>
                  <th scope="col">{t("state")}</th>
                  <th scope="col">{t("entries")}</th>
                  <th scope="col">{t("actions")}</th>
                </tr>
              </thead>
              <tbody>
                {windows.map((w) => (
                  <tr key={w.id}>
                    <th scope="row">{name(w.useCase)}</th>
                    <td>{when(w.openedAt)}</td>
                    <td>{when(w.closedAt ?? w.endsAt)}</td>
                    <td>{t(w.open ? "states.open" : "states.closed")}</td>
                    <td data-number="">{format.number(w.entries)}</td>
                    <td>
                      <div className="ui-control-row">
                        <Link href={`/admin/ai/debug/${w.id}`}>{t("view", { useCase: name(w.useCase) })}</Link>
                        {canManage && w.open ? (
                          <Button
                            variant="destructive"
                            disabled={busy}
                            onClick={() => void call(`/api/v1/ai/debug-windows/${w.id}`, "DELETE", null, (c) => t("closed", { useCase: name(c.useCase) }))}
                          >
                            {t("close", { useCase: name(w.useCase) })}
                          </Button>
                        ) : null}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
