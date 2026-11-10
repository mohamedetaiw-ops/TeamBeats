"use client";

import { useId, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { RUN_FILTERS, RUN_STATUSES, known, type ReplayResult, type RunPage, type RunView } from "@/lib/integrations/types";
import { ErrorText, Messages } from "./parts";
import { send } from "./request";

/**
 * Run history of one connection (spec 017 US3): status, attempts, error class, business code, external reference and
 * duration, never the data sent or received. Failed and unknown runs from the last 14 days can be replayed with their
 * original idempotency key. The API decides who may see and replay runs (<c>integration.operate</c>).
 */
export function RunHistory({ connectionId, initial }: { connectionId: string; initial: RunPage | null }) {
  const t = useTranslations("connections.runs");
  const format = useFormatter();
  const filterId = useId();
  const [runs, setRuns] = useState<RunView[]>(initial?.items ?? []);
  const [cursor, setCursor] = useState(initial?.nextCursor ?? null);
  const [filter, setFilter] = useState<string>("all");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const base = `/api/v1/integrations/connections/${connectionId}/runs`;

  if (initial === null) {
    return (
      <section className="connections-section" aria-labelledby="runs-heading">
        <h2 id="runs-heading">{t("title")}</h2>
        <p className="notice">{t("unavailable")}</p>
      </section>
    );
  }

  async function load(next: string, after: string | null) {
    setBusy("load");
    try {
      const params = new URLSearchParams();
      if (next !== "all") params.set("status", next);
      if (after) params.set("cursor", after);
      const response = await fetch(`${base}?${params.toString()}`);
      if (response.ok) {
        const page = (await response.json()) as RunPage;
        setRuns((list) => (after ? [...list, ...page.items] : page.items));
        setCursor(page.nextCursor);
      } else {
        setMessages({ ok: false, list: [t("failed")] });
      }
    } catch {
      setMessages({ ok: false, list: [t("failed")] });
    } finally {
      setBusy(null);
    }
  }

  async function replay() {
    setBusy("replay");
    setMessages({ ok: true, list: [] });
    const result = await send<ReplayResult>("POST", `${base}:replay`, { runIds: [...selected] }, `/admin/connections/${connectionId}`, t("failed"));
    setBusy(null);
    if (result.ok) {
      setSelected(new Set());
      setMessages({ ok: true, list: [t("replayed", { replayed: result.data.replayed.length, skipped: result.data.skipped.length })] });
      await load(filter, null);
    } else {
      setMessages({ ok: false, list: result.messages });
    }
  }

  const date = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" });

  return (
    <section className="connections-section" aria-labelledby="runs-heading">
      <h2 id="runs-heading">{t("title")}</h2>
      <p>{t("intro")}</p>
      <div className="field">
        <label htmlFor={filterId}>{t("filter")}</label>
        <select
          id={filterId}
          value={filter}
          onChange={(e) => {
            setFilter(e.target.value);
            setSelected(new Set());
            void load(e.target.value, null);
          }}
        >
          {RUN_FILTERS.map((f) => (
            <option key={f} value={f}>
              {t(`filters.${f}`)}
            </option>
          ))}
        </select>
      </div>
      {runs.length === 0 ? (
        <p className="notice">{t("empty")}</p>
      ) : (
        <ul className="connections-list">
          {runs.map((r) => (
            <li key={r.id} className="connections-item" data-status={r.status}>
              <h3 className="connections-row">
                {r.replayable ? (
                  <input
                    type="checkbox"
                    aria-label={t("select", { action: r.action, at: date(r.createdAt) })}
                    checked={selected.has(r.id)}
                    onChange={(e) =>
                      setSelected((s) => {
                        const next = new Set(s);
                        if (e.target.checked) next.add(r.id);
                        else next.delete(r.id);
                        return next;
                      })
                    }
                  />
                ) : null}
                <bdi dir="ltr">{r.action}</bdi>
                <span className="connections-status" data-status={r.status}>
                  {known(RUN_STATUSES, r.status) ? t(`statuses.${r.status}`) : r.status}
                </span>
              </h3>
              <p className="connections-row">
                <span>{date(r.createdAt)}</span>
                <span>{t("attempts", { count: r.attempts })}</span>
                {r.durationMs != null ? <span>{t("duration", { ms: r.durationMs })}</span> : null}
                {r.replays > 0 ? <span>{t("replays", { count: r.replays })}</span> : null}
              </p>
              {r.errorClass ? (
                <p>
                  <ErrorText errorClass={r.errorClass} />
                  {r.errorCode ? (
                    <>
                      {" "}
                      <bdi dir="ltr">{r.errorCode}</bdi>
                    </>
                  ) : null}
                </p>
              ) : null}
              {r.externalReference ? (
                <p>
                  {t("reference")} <bdi dir="ltr">{r.externalReference}</bdi>
                </p>
              ) : null}
              {r.correlationId ? (
                <p>
                  {t("correlation")} <bdi dir="ltr">{r.correlationId}</bdi>
                </p>
              ) : null}
            </li>
          ))}
        </ul>
      )}
      <div className="connections-row">
        {cursor ? (
          <Button busy={busy === "load"} busyLabel={t("working")} onClick={() => void load(filter, cursor)}>
            {t("more")}
          </Button>
        ) : null}
        <Button variant="primary" disabled={selected.size === 0} busy={busy === "replay"} busyLabel={t("working")} onClick={() => void replay()}>
          {t("replay", { count: selected.size })}
        </Button>
      </div>
      <Messages messages={messages.list} ok={messages.ok} />
    </section>
  );
}
