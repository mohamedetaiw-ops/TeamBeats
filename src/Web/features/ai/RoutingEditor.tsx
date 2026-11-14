"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import { USE_CASES, type CatalogueEntry, type ModelPolicyView, type PolicySaveResult } from "@/lib/ai/types";
import { modelName } from "./PolicyEditor";

type Result = { ok: boolean; messages: string[] } | null;

function RouteEditor({
  useCase,
  route,
  allowed,
  editable,
  name,
  onChange,
}: {
  useCase: string;
  route: string[];
  allowed: string[];
  editable: boolean;
  name: (model: string) => string;
  onChange: (route: string[]) => void;
}) {
  const t = useTranslations("ai.routing");
  const addId = useId();
  const [adding, setAdding] = useState("");
  const left = allowed.filter((m) => !route.includes(m));
  const label = t(`useCases.${useCase}`);
  function move(from: number, to: number) {
    const next = [...route];
    const [m] = next.splice(from, 1);
    if (m !== undefined) next.splice(to, 0, m);
    onChange(next);
  }
  return (
    <section className="ai-section" aria-labelledby={`${addId}-title`} data-testid={`ai-route-${useCase}`}>
      <h2 id={`${addId}-title`}>{label}</h2>
      {route.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <ol className="ai-route">
          {route.map((m, i) => (
            <li key={m}>
              <span>
                {t(i === 0 ? "default" : "fallback", { position: String(i) })}: <bdi>{name(m)}</bdi>
              </span>
              {editable ? (
                <>
                  <Button variant="quiet" disabled={i === 0} onClick={() => move(i, i - 1)} aria-label={t("up", { name: name(m), useCase: label })}>
                    {t("upShort")}
                  </Button>
                  <Button
                    variant="quiet"
                    disabled={i === route.length - 1}
                    onClick={() => move(i, i + 1)}
                    aria-label={t("down", { name: name(m), useCase: label })}
                  >
                    {t("downShort")}
                  </Button>
                  <Button variant="quiet" onClick={() => onChange(route.filter((x) => x !== m))} aria-label={t("remove", { name: name(m), useCase: label })}>
                    {t("removeShort")}
                  </Button>
                </>
              ) : null}
            </li>
          ))}
        </ol>
      )}
      {editable && left.length > 0 ? (
        <div className="ui-control-row">
          <div className="field">
            <label htmlFor={addId}>{t("add", { useCase: label })}</label>
            <select id={addId} value={adding} onChange={(e) => setAdding(e.target.value)}>
              <option value="">{t("choose")}</option>
              {left.map((m) => (
                <option key={m} value={m}>
                  {name(m)}
                </option>
              ))}
            </select>
          </div>
          <Button
            disabled={adding === ""}
            onClick={() => {
              onChange([...route, adding]);
              setAdding("");
            }}
          >
            {t("addButton")}
          </Button>
        </div>
      ) : null}
    </section>
  );
}

/**
 * Routing rules (spec 013 US1, FR-019): the default and fallback models of each use case, from the allowed models.
 * A change to the assistant or agent route is held for the release gate; until spec 034 provides it, an administrator
 * applies the held change with a reason. The API validates every route and decides who may do what.
 */
export function RoutingEditor({ catalogue, initial, canOverride }: { catalogue: CatalogueEntry[]; initial: ModelPolicyView; canOverride: boolean }) {
  const t = useTranslations("ai.routing");
  const locale = useLocale();
  const reasonId = useId();
  const [view, setView] = useState(initial);
  const [routes, setRoutes] = useState<Record<string, string[]>>(() => Object.fromEntries(USE_CASES.map((u) => [u, initial.policy.routes[u] ?? []])));
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const byModel = new Map(catalogue.map((m) => [m.model, m]));
  const name = (model: string) => modelName(byModel.get(model), model, locale);
  const embeds = (m: string) => byModel.get(m)?.capabilities.includes("embeddings") ?? false;
  const chats = (m: string) => byModel.get(m)?.capabilities.includes("chat") ?? false;

  async function send(url: string, method: string, body: unknown, ok: (r: PolicySaveResult) => string) {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(url, {
        method,
        headers: { "content-type": "application/json", ...(method === "PUT" ? { "if-match": `"v${view.version}"` } : {}) },
        body: JSON.stringify(body),
      });
      if (response.ok) {
        const saved = (await response.json()) as PolicySaveResult;
        setView(saved.policy);
        setResult({ ok: true, messages: [ok(saved)] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  const pending = view.pending;
  return (
    <div className="ai">
      {view.readOnlyReason ? (
        <p className="notice">{t(view.readOnlyReason === "ai.policy.read_only_package" ? "readOnly.package" : "readOnly.permission")}</p>
      ) : null}
      <p>{t("intro")}</p>

      {pending ? (
        <section className="ai-section" aria-labelledby={`${reasonId}-pending`} data-testid="ai-pending-change">
          <h2 id={`${reasonId}-pending`}>{t("pending.title")}</h2>
          <p data-testid="ai-pending-gate">{t(`pending.gateStatus.${pending.gateStatus ?? "running"}`)}</p>
          <ul>
            {pending.useCases.map((u) => (
              <li key={u}>
                {t("pending.change", {
                  useCase: t(`useCases.${u}`),
                  before: (pending.before[u] ?? []).map(name).join(", ") || t("pending.none"),
                  after: (pending.after[u] ?? []).map(name).join(", ") || t("pending.none"),
                })}
              </li>
            ))}
          </ul>
          {pending.failures.length > 0 ? (
            <>
              <h3>{t("pending.failures")}</h3>
              <ul data-testid="ai-pending-failures">
                {pending.failures.map((f) => (
                  <li key={f}>
                    <bdi dir="ltr">{f}</bdi>
                  </li>
                ))}
              </ul>
            </>
          ) : null}
          {canOverride && pending.canOverride ? (
            <>
              <div className="field">
                <label htmlFor={reasonId}>{t("pending.reason")}</label>
                <p id={`${reasonId}-hint`} className="field-hint">
                  {t("pending.reasonHint")}
                </p>
                <textarea id={reasonId} aria-describedby={`${reasonId}-hint`} value={reason} onChange={(e) => setReason(e.target.value)} />
              </div>
              <div className="ui-control-row">
                <Button
                  variant="primary"
                  busy={busy}
                  busyLabel={t("saving")}
                  onClick={() => void send(`/api/v1/ai/route-changes/${pending.id}/override`, "POST", { reason }, () => t("pending.applied"))}
                >
                  {t("pending.override")}
                </Button>
              </div>
            </>
          ) : null}
        </section>
      ) : null}

      {USE_CASES.map((u) => (
        <RouteEditor
          key={u}
          useCase={u}
          route={routes[u] ?? []}
          allowed={view.policy.allowlist.filter(u === "embed" ? embeds : chats)}
          editable={view.editable}
          name={name}
          onChange={(route) => setRoutes({ ...routes, [u]: route })}
        />
      ))}

      {result ? (
        <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
          {result.messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}

      {view.editable ? (
        <div className="ui-control-row">
          <Button variant="primary" busy={busy} busyLabel={t("saving")} onClick={() => void send("/api/v1/ai/policy", "PUT", { routes }, (r) => t(r.applied ? "saved" : "held"))}>
            {t("save")}
          </Button>
        </div>
      ) : null}
    </div>
  );
}
