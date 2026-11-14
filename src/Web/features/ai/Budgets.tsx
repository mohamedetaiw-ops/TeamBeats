"use client";

import { useId, useState, type FormEvent } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { BUDGET_ACTIONS, BUDGET_SCOPES, type Budget, type BudgetsView } from "@/lib/ai/types";
import type { AgentSummary } from "@/lib/agents/types";
import type { OrgUnitView } from "@/lib/admin/types";
import { useHydrated } from "@/lib/clock";
import { PersonPicker } from "@/features/services/PersonPicker";

type Result = { ok: boolean; messages: string[] } | null;
type Draft = {
  scope: Budget["scope"];
  scopeId: string | null;
  period: Budget["period"];
  kind: "amount" | "requests";
  limit: string;
  action: Budget["action"];
  downgradeModel: string;
  ownerId: string | null;
};

const empty: Draft = { scope: "tenant", scopeId: null, period: "month", kind: "amount", limit: "", action: "alert", downgradeModel: "", ownerId: null };

function pick(names: Record<string, string> | undefined | null, locale: string): string {
  if (!names) return "";
  return names[locale] ?? names.default ?? names.en ?? Object.values(names)[0] ?? "";
}

/**
 * AI budgets (spec 013 US5, FR-012): the AI Policy Administrator sets a USD amount or a request count per month or day
 * for the whole organization, a unit, an agent, one person or each person, and what happens when it is used up. Which
 * scopes the package allows, who may change budgets, and every limit are the API's rules; this screen sends the form
 * and shows the answer.
 */
export function Budgets({ initial, models, units, agents }: { initial: BudgetsView; models: string[]; units: OrgUnitView[]; agents: AgentSummary[] }) {
  const t = useTranslations("ai.budgets");
  const format = useFormatter();
  const locale = useLocale();
  const hydrated = useHydrated();
  const [view, setView] = useState(initial);
  const [draft, setDraft] = useState<Draft>(empty);
  const [editing, setEditing] = useState<Budget | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const ids = { scope: useId(), scopeId: useId(), period: useId(), kind: useId(), action: useId(), model: useId() };
  const when = (iso: string) => (hydrated ? format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" }) : "");
  const usd = (value: number) => format.number(value, { style: "currency", currency: "USD" });

  function who(b: Budget): string {
    switch (b.scope) {
      case "org_unit":
        return pick(view.labels[b.scopeId ?? ""], locale) || pick(units.find((u) => u.id === b.scopeId)?.names, locale) || t("unknown");
      case "agent": {
        const agent = agents.find((a) => a.id === b.scopeId);
        return agent ? pick(agent.name, locale) || t("unknown") : t("unknown");
      }
      case "user":
        return pick(view.labels[b.scopeId ?? ""], locale) || t("unknown");
      default:
        return t(`scopes.${b.scope}`);
    }
  }

  function limit(b: Budget): string {
    return b.amount !== null ? usd(b.amount) : t("requestCount", { count: b.requests ?? 0 });
  }

  function used(b: Budget): string {
    if (b.used === null) return t("perPerson");
    return b.amount !== null ? usd(b.used) : t("requestCount", { count: b.used });
  }

  async function reload() {
    const response = await fetch("/api/v1/ai/budgets", { cache: "no-store" });
    if (response.ok) setView((await response.json()) as BudgetsView);
  }

  async function send(url: string, method: string, body: unknown, done: string) {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(url, {
        method,
        headers: body === null ? undefined : { "content-type": "application/json" },
        body: body === null ? undefined : JSON.stringify(body),
      });
      if (response.ok) {
        await reload();
        setResult({ ok: true, messages: [done] });
        setEditing(null);
        setDraft(empty);
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    const value = Number(draft.limit);
    const body = {
      scope: draft.scope,
      scopeId: draft.scope === "org_unit" || draft.scope === "agent" || draft.scope === "user" ? draft.scopeId : null,
      period: draft.period,
      amount: draft.kind === "amount" ? value : null,
      requests: draft.kind === "requests" ? value : null,
      action: draft.action,
      downgradeModel: draft.action === "downgrade" ? draft.downgradeModel || null : null,
      ownerId: draft.ownerId,
      version: editing?.version,
    };
    if (editing) void send(`/api/v1/ai/budgets/${encodeURIComponent(editing.id)}`, "PUT", body, t("saved"));
    else void send("/api/v1/ai/budgets", "POST", body, t("created"));
  }

  function edit(b: Budget) {
    setEditing(b);
    setResult(null);
    setDraft({
      scope: b.scope,
      scopeId: b.scopeId,
      period: b.period,
      kind: b.amount !== null ? "amount" : "requests",
      limit: String(b.amount ?? b.requests ?? ""),
      action: b.action,
      downgradeModel: b.downgradeModel ?? "",
      ownerId: b.ownerId,
    });
  }

  const columns: Column<Budget>[] = [
    { key: "who", header: t("who"), rowHeader: true, cell: (b) => <bdi>{who(b)}</bdi> },
    { key: "period", header: t("period"), cell: (b) => t(`periods.${b.period}`) },
    { key: "limit", header: t("limit"), align: "end", cell: (b) => limit(b) },
    { key: "used", header: t("used"), align: "end", cell: (b) => used(b) },
    { key: "resets", header: t("resets"), priority: "low", cell: (b) => when(b.resetsAt) },
    {
      key: "action",
      header: t("action"),
      cell: (b) => (b.action === "downgrade" ? t("downgradeTo", { model: b.downgradeModel ?? "" }) : t(`actions.${b.action}`)),
    },
    { key: "owner", header: t("owner"), priority: "low", cell: (b) => (b.ownerId ? <bdi>{pick(view.labels[b.ownerId], locale) || t("unknown")}</bdi> : t("noOwner")) },
  ];
  if (view.editable) {
    columns.push({
      key: "manage",
      header: t("manage"),
      align: "end",
      cell: (b) => (
        <div className="button-row">
          <Button variant="quiet" size="sm" aria-label={t("change", { who: who(b) })} onClick={() => edit(b)}>
            {t("changeShort")}
          </Button>
          <Button
            variant="destructive"
            size="sm"
            disabled={busy}
            aria-label={t("remove", { who: who(b) })}
            onClick={() => void send(`/api/v1/ai/budgets/${encodeURIComponent(b.id)}`, "DELETE", null, t("removed"))}
          >
            {t("removeShort")}
          </Button>
        </div>
      ),
    });
  }

  const scopes = BUDGET_SCOPES.filter((s) => s === "tenant" || view.scopedAllowed);

  return (
    <div className="ai">
      <p>{t("intro")}</p>
      {view.readOnlyReason ? <p className="notice">{t(view.readOnlyReason === "ai.budgets.read_only_package" ? "readOnly.package" : "readOnly.permission")}</p> : null}
      <DataTable
        caption={t("caption")}
        columns={columns}
        rows={view.budgets}
        rowKey={(b) => b.id}
        emptyTitle={t("emptyTitle")}
        emptyMessage={t("empty")}
        density="compact"
      />

      {view.editable ? (
        <form className="ai-section" aria-labelledby="ai-budget-form" onSubmit={submit} data-testid="ai-budget-form">
          <h2 id="ai-budget-form">{editing ? t("editTitle", { who: who(editing) }) : t("addTitle")}</h2>
          {!view.scopedAllowed ? <p className="field-hint">{t("scopedHint")}</p> : null}
          <div className="ai-row">
            <div className="field">
              <label htmlFor={ids.scope}>{t("scope")}</label>
              <select
                id={ids.scope}
                value={draft.scope}
                disabled={editing !== null}
                onChange={(e) => setDraft({ ...draft, scope: e.target.value as Draft["scope"], scopeId: null })}
              >
                {scopes.map((s) => (
                  <option key={s} value={s}>
                    {t(`scopes.${s}`)}
                  </option>
                ))}
              </select>
            </div>
            {draft.scope === "org_unit" ? (
              <div className="field">
                <label htmlFor={ids.scopeId}>{t("unit")}</label>
                <select id={ids.scopeId} value={draft.scopeId ?? ""} disabled={editing !== null} onChange={(e) => setDraft({ ...draft, scopeId: e.target.value || null })} required>
                  <option value="">{t("choose")}</option>
                  {units.map((u) => (
                    <option key={u.id} value={u.id}>
                      {pick(u.names, locale)}
                    </option>
                  ))}
                </select>
              </div>
            ) : null}
            {draft.scope === "agent" ? (
              <div className="field">
                <label htmlFor={ids.scopeId}>{t("agent")}</label>
                <select id={ids.scopeId} value={draft.scopeId ?? ""} disabled={editing !== null} onChange={(e) => setDraft({ ...draft, scopeId: e.target.value || null })} required>
                  <option value="">{t("choose")}</option>
                  {agents.map((a) => (
                    <option key={a.id} value={a.id}>
                      {pick(a.name, locale)}
                    </option>
                  ))}
                </select>
              </div>
            ) : null}
            <div className="field">
              <label htmlFor={ids.period}>{t("period")}</label>
              <select id={ids.period} value={draft.period} disabled={editing !== null} onChange={(e) => setDraft({ ...draft, period: e.target.value as Draft["period"] })}>
                <option value="month">{t("periods.month")}</option>
                <option value="day">{t("periods.day")}</option>
              </select>
            </div>
          </div>
          {draft.scope === "user" ? <PersonPicker label={t("person")} value={draft.scopeId} onChange={(id) => setDraft({ ...draft, scopeId: id })} readOnly={editing !== null} /> : null}
          <div className="ai-row">
            <div className="field">
              <label htmlFor={ids.kind}>{t("kind")}</label>
              <select id={ids.kind} value={draft.kind} onChange={(e) => setDraft({ ...draft, kind: e.target.value as Draft["kind"] })}>
                <option value="amount">{t("kinds.amount")}</option>
                <option value="requests">{t("kinds.requests")}</option>
              </select>
            </div>
            <FormField
              label={draft.kind === "amount" ? t("amount") : t("requests")}
              name="limit"
              type="number"
              min={draft.kind === "amount" ? 0.01 : 1}
              step={draft.kind === "amount" ? 0.01 : 1}
              value={draft.limit}
              onChange={(e) => setDraft({ ...draft, limit: e.target.value })}
              required
            />
            <div className="field">
              <label htmlFor={ids.action}>{t("action")}</label>
              <select id={ids.action} value={draft.action} onChange={(e) => setDraft({ ...draft, action: e.target.value as Draft["action"] })}>
                {BUDGET_ACTIONS.map((a) => (
                  <option key={a} value={a}>
                    {t(`actions.${a}`)}
                  </option>
                ))}
              </select>
            </div>
            {draft.action === "downgrade" ? (
              <div className="field">
                <label htmlFor={ids.model}>{t("downgradeModel")}</label>
                <select id={ids.model} value={draft.downgradeModel} onChange={(e) => setDraft({ ...draft, downgradeModel: e.target.value })} required>
                  <option value="">{t("choose")}</option>
                  {models.map((m) => (
                    <option key={m} value={m}>
                      {m}
                    </option>
                  ))}
                </select>
              </div>
            ) : null}
          </div>
          <PersonPicker label={t("ownerPicker")} value={draft.ownerId} onChange={(id) => setDraft({ ...draft, ownerId: id })} />
          <div className="button-row">
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
