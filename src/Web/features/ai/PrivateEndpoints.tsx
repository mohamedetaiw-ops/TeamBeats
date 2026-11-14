"use client";

import { useId, useState, type FormEvent } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { useHydrated } from "@/lib/clock";
import { CLASSIFICATIONS, MODEL_CAPABILITIES, type PrivateEndpoint, type PrivateEndpointsView, type PrivateModel } from "@/lib/ai/types";

type Result = { ok: boolean; messages: string[] } | null;
type ModelDraft = { name: string; capabilities: string[]; contextTokens: string };
type Draft = { name: string; baseUrl: string; secretRef: string; region: string; maxClassification: string; models: ModelDraft[] };

const blankModel: ModelDraft = { name: "", capabilities: ["chat"], contextTokens: "" };
const empty: Draft = { name: "", baseUrl: "", secretRef: "", region: "", maxClassification: "confidential", models: [blankModel] };

const toDraft = (e: PrivateEndpoint): Draft => ({
  name: e.name,
  baseUrl: e.baseUrl,
  secretRef: e.secretRef,
  region: e.region,
  maxClassification: e.maxClassification,
  models: e.models.map((m) => ({ name: m.name, capabilities: m.capabilities, contextTokens: String(m.contextTokens) })),
});

const CHECK_DETAILS = ["secret_missing", "no_adapter", "rejected", "unreachable", "blocked_address", "model_missing", "call_failed"] as const;

/**
 * Private model endpoints (spec 013 US6, T025): a tenant's own OpenAI-compatible inference servers. The administrator
 * registers the address, the secret manager reference of its key, the models with their capabilities, the region and
 * the highest classification; runs the connection and capability test; and enables the endpoint once a test passed in
 * the last 24 hours. Every rule is the API's; the key itself never reaches this page.
 */
export function PrivateEndpoints({ initial }: { initial: PrivateEndpointsView }) {
  const t = useTranslations("ai.endpoints");
  const format = useFormatter();
  const hydrated = useHydrated();
  const [view, setView] = useState(initial);
  const [draft, setDraft] = useState<Draft>(empty);
  const [editing, setEditing] = useState<PrivateEndpoint | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const ids = { level: useId() };
  const when = (iso: string) => (hydrated ? format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" }) : "");
  const shown = view.endpoints.find((e) => e.id === selected) ?? null;

  async function reload() {
    const response = await fetch("/api/v1/ai/private-endpoints", { cache: "no-store" });
    if (response.ok) setView((await response.json()) as PrivateEndpointsView);
  }

  async function send(url: string, method: string, body: unknown, done: (e: PrivateEndpoint | null) => string) {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(url, {
        method,
        headers: body === null ? undefined : { "content-type": "application/json" },
        body: body === null ? undefined : JSON.stringify(body),
      });
      if (response.ok) {
        const endpoint = response.status === 204 ? null : ((await response.json()) as PrivateEndpoint);
        await reload();
        setResult({ ok: true, messages: [done(endpoint)] });
        return endpoint;
      }
      setResult({ ok: false, messages: await problemMessages(response) });
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
    return null;
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    const models: PrivateModel[] = draft.models.map((m) => ({ name: m.name.trim(), capabilities: m.capabilities, contextTokens: Number(m.contextTokens) }));
    const body = { ...draft, models, version: editing?.version };
    const saved = editing
      ? await send(`/api/v1/ai/private-endpoints/${encodeURIComponent(editing.id)}`, "PUT", body, (x) => t("saved", { name: x?.name ?? "" }))
      : await send("/api/v1/ai/private-endpoints", "POST", body, (x) => t("created", { name: x?.name ?? "" }));
    if (saved) {
      setEditing(null);
      setDraft(empty);
      setSelected(saved.id);
    }
  }

  async function act(endpoint: PrivateEndpoint, action: "test" | "enable" | "disable") {
    const done = await send(`/api/v1/ai/private-endpoints/${encodeURIComponent(endpoint.id)}/${action}`, "POST", null, (x) =>
      action === "test" ? t(x?.lastTestPassed ? "testPassed" : "testFailed", { name: endpoint.name }) : t(action === "enable" ? "enabled" : "disabled", { name: endpoint.name }),
    );
    if (done) setSelected(endpoint.id);
  }

  const setModel = (index: number, change: Partial<ModelDraft>) =>
    setDraft({ ...draft, models: draft.models.map((m, i) => (i === index ? { ...m, ...change } : m)) });

  const columns: Column<PrivateEndpoint>[] = [
    { key: "name", header: t("name"), rowHeader: true, cell: (e) => <bdi>{e.name}</bdi> },
    { key: "address", header: t("baseUrl"), priority: "low", cell: (e) => <bdi dir="ltr">{e.baseUrl}</bdi> },
    { key: "models", header: t("models"), cell: (e) => t("modelCount", { count: e.models.length }) },
    { key: "level", header: t("maxClassification"), cell: (e) => t(`levels.${e.maxClassification}`) },
    {
      key: "status",
      header: t("status"),
      cell: (e) => <Badge tone={e.status === "enabled" ? "success" : "neutral"}>{t(`statuses.${e.status}`)}</Badge>,
    },
    {
      key: "test",
      header: t("lastTest"),
      priority: "low",
      cell: (e) =>
        e.lastTestAt ? t(e.lastTestPassed ? (e.testCurrent ? "passedAt" : "expiredAt") : "failedAt", { when: when(e.lastTestAt) }) : t("neverTested"),
    },
    {
      key: "manage",
      header: t("manage"),
      align: "end",
      cell: (e) => (
        <div className="button-row">
          <Button variant="quiet" size="sm" aria-label={t("detailsFor", { name: e.name })} onClick={() => setSelected(e.id)}>
            {t("details")}
          </Button>
          {view.editable ? (
            <>
              <Button variant="secondary" size="sm" disabled={busy} aria-label={t("testFor", { name: e.name })} onClick={() => void act(e, "test")}>
                {t("test")}
              </Button>
              {e.status === "enabled" ? (
                <Button variant="secondary" size="sm" disabled={busy} aria-label={t("disableFor", { name: e.name })} onClick={() => void act(e, "disable")}>
                  {t("disable")}
                </Button>
              ) : (
                <Button variant="primary" size="sm" disabled={busy} aria-label={t("enableFor", { name: e.name })} onClick={() => void act(e, "enable")}>
                  {t("enable")}
                </Button>
              )}
              <Button
                variant="quiet"
                size="sm"
                aria-label={t("changeFor", { name: e.name })}
                onClick={() => {
                  setEditing(e);
                  setDraft(toDraft(e));
                  setResult(null);
                }}
              >
                {t("change")}
              </Button>
              {e.status !== "enabled" ? (
                <Button
                  variant="destructive"
                  size="sm"
                  disabled={busy}
                  aria-label={t("removeFor", { name: e.name })}
                  onClick={() => void send(`/api/v1/ai/private-endpoints/${encodeURIComponent(e.id)}`, "DELETE", null, () => t("removed", { name: e.name }))}
                >
                  {t("remove")}
                </Button>
              ) : null}
            </>
          ) : null}
        </div>
      ),
    },
  ];

  const detail = (key: string | null) => (key && (CHECK_DETAILS as readonly string[]).includes(key.replace("ai.endpoints.checks.", "")) ? t(`checks.${key.replace("ai.endpoints.checks.", "")}`) : null);

  return (
    <div className="ai">
      <p>{t("intro")}</p>
      {view.readOnlyReason ? <p className="notice">{t(view.readOnlyReason === "ai.endpoints.read_only_package" ? "readOnly.package" : "readOnly.permission")}</p> : null}
      <DataTable
        caption={t("caption")}
        columns={columns}
        rows={view.endpoints}
        rowKey={(e) => e.id}
        emptyTitle={t("emptyTitle")}
        emptyMessage={t("empty")}
        density="compact"
      />

      {shown ? (
        <section className="ai-section" aria-labelledby="ai-endpoint-detail" data-testid="ai-endpoint-detail">
          <h2 id="ai-endpoint-detail">{t("detailTitle", { name: shown.name })}</h2>
          <p className="field-hint">{t("catalogueNames")}</p>
          <ul>
            {shown.catalogueModels.map((m) => (
              <li key={m}>
                <bdi dir="ltr">{m}</bdi>
              </li>
            ))}
          </ul>
          {shown.checks.length > 0 ? (
            <>
              <h3>{t("checksTitle")}</h3>
              <ul data-testid="ai-endpoint-checks">
                {shown.checks.map((c, i) => (
                  <li key={`${c.check}-${c.model ?? ""}-${i}`}>
                    <Badge tone={c.passed ? "success" : "danger"}>{t(c.passed ? "checkPassed" : "checkFailed")}</Badge>{" "}
                    {c.model ? t("checkOnModel", { check: t(`checkNames.${c.check}`), model: c.model }) : t(`checkNames.${c.check}`)}
                    {detail(c.detail) ? <span className="field-hint"> {detail(c.detail)}</span> : null}
                  </li>
                ))}
              </ul>
            </>
          ) : (
            <p>{t("neverTested")}</p>
          )}
        </section>
      ) : null}

      {view.editable ? (
        <form className="ai-section" aria-labelledby="ai-endpoint-form" onSubmit={(e) => void submit(e)} data-testid="ai-endpoint-form">
          <h2 id="ai-endpoint-form">{editing ? t("editTitle", { name: editing.name }) : t("addTitle")}</h2>
          {editing ? <p className="field-hint">{t("changeDisables")}</p> : null}
          <div className="ai-row">
            <FormField label={t("name")} name="name" required maxLength={80} value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
            <FormField
              label={t("baseUrl")}
              hint={t("baseUrlHint")}
              name="baseUrl"
              type="url"
              dir="ltr"
              required
              value={draft.baseUrl}
              onChange={(e) => setDraft({ ...draft, baseUrl: e.target.value })}
            />
          </div>
          <div className="ai-row">
            <FormField
              label={t("secretRef")}
              hint={t("secretRefHint")}
              name="secretRef"
              dir="ltr"
              required
              value={draft.secretRef}
              onChange={(e) => setDraft({ ...draft, secretRef: e.target.value })}
            />
            <FormField label={t("region")} hint={t("regionHint")} name="region" dir="ltr" required value={draft.region} onChange={(e) => setDraft({ ...draft, region: e.target.value })} />
            <div className="field">
              <label htmlFor={ids.level}>{t("maxClassification")}</label>
              <select id={ids.level} value={draft.maxClassification} onChange={(e) => setDraft({ ...draft, maxClassification: e.target.value })}>
                {CLASSIFICATIONS.map((c) => (
                  <option key={c} value={c}>
                    {t(`levels.${c}`)}
                  </option>
                ))}
              </select>
            </div>
          </div>
          {draft.models.map((m, i) => (
            <fieldset key={i} className="ai-section">
              <legend>{t("modelLegend", { number: i + 1 })}</legend>
              <div className="ai-row">
                <FormField label={t("modelName")} name={`model-${i}`} dir="ltr" required value={m.name} onChange={(e) => setModel(i, { name: e.target.value })} />
                <FormField
                  label={t("contextTokens")}
                  name={`context-${i}`}
                  type="number"
                  min={1}
                  required
                  value={m.contextTokens}
                  onChange={(e) => setModel(i, { contextTokens: e.target.value })}
                />
              </div>
              {MODEL_CAPABILITIES.map((c) => (
                <Checkbox
                  key={c}
                  label={t(`capabilities.${c}`)}
                  checked={m.capabilities.includes(c)}
                  onChange={(e) => setModel(i, { capabilities: e.target.checked ? [...m.capabilities.filter((x) => x !== c), c] : m.capabilities.filter((x) => x !== c) })}
                />
              ))}
              {draft.models.length > 1 ? (
                <Button variant="quiet" size="sm" onClick={() => setDraft({ ...draft, models: draft.models.filter((_, j) => j !== i) })}>
                  {t("removeModel", { number: i + 1 })}
                </Button>
              ) : null}
            </fieldset>
          ))}
          <div className="button-row">
            <Button variant="secondary" onClick={() => setDraft({ ...draft, models: [...draft.models, blankModel] })}>
              {t("addModel")}
            </Button>
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
              {editing ? t("save") : t("add")}
            </Button>
            {editing ? (
              <Button
                variant="secondary"
                onClick={() => {
                  setEditing(null);
                  setDraft(empty);
                }}
              >
                {t("cancel")}
              </Button>
            ) : null}
          </div>
        </form>
      ) : null}

      <div aria-live="polite">
        {result ? (
          <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
            {result.messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
      </div>
    </div>
  );
}
