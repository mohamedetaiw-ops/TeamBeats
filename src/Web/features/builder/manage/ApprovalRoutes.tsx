"use client";

import { useId, useState, type FormEvent } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { unitName, type OrgUnit, type PageRoute } from "@/lib/experience/builder-types";
import { useHydrated } from "@/lib/clock";
import { PersonPicker } from "@/features/services/PersonPicker";
import { SendMessages, useSend } from "./useSend";

/**
 * Page approval routes (spec 032 R2 T023, FR-051): who approves pages published at a scope. The nearest route up the org
 * tree applies, then the whole organization's. Which scopes the editor may route, and every rule, are the API's.
 */
export function ApprovalRoutes({ initial, units }: { initial: PageRoute[]; units: OrgUnit[] }) {
  const t = useTranslations("builder.manage.routes");
  const locale = useLocale();
  const format = useFormatter();
  const hydrated = useHydrated();
  const scopeId = useId();
  const [routes, setRoutes] = useState(initial);
  const [unit, setUnit] = useState<string>("");
  const [approvers, setApprovers] = useState<string[]>([]);
  const [editing, setEditing] = useState<PageRoute | null>(null);
  const { busy, result, send } = useSend(t("failed"));

  const scopeName = (id: string | null) =>
    id === null ? t("wholeTenant") : (units.find((u) => u.id === id) ? unitName(units.find((u) => u.id === id)!, locale) : t("unknownUnit"));

  async function reload() {
    const response = await fetch("/api/v1/experience/page-routes", { cache: "no-store" });
    if (response.ok) setRoutes((await response.json()) as PageRoute[]);
  }

  function reset() {
    setEditing(null);
    setUnit("");
    setApprovers([]);
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    const saved = await send("/api/v1/experience/page-routes", "PUT", { orgUnitId: unit || null, approverIds: approvers }, editing ? t("saved") : t("added"));
    if (saved) {
      await reload();
      reset();
    }
  }

  const columns: Column<PageRoute>[] = [
    { key: "scope", header: t("scope"), rowHeader: true, cell: (r) => <bdi>{scopeName(r.orgUnitId)}</bdi> },
    { key: "approvers", header: t("approvers"), align: "end", cell: (r) => String(r.approverIds.length) },
    {
      key: "changed",
      header: t("changed"),
      priority: "low",
      cell: (r) => (hydrated ? <time dateTime={r.updatedAt}>{format.dateTime(new Date(r.updatedAt), { dateStyle: "medium", timeStyle: "short" })}</time> : null),
    },
    {
      key: "manage",
      header: t("manage"),
      align: "end",
      cell: (r) => (
        <div className="button-row">
          <Button
            variant="quiet"
            size="sm"
            aria-label={t("changeLabel", { scope: scopeName(r.orgUnitId) })}
            onClick={() => {
              setEditing(r);
              setUnit(r.orgUnitId ?? "");
              setApprovers(r.approverIds);
            }}
          >
            {t("change")}
          </Button>
          <ConfirmAction
            label={t("remove")}
            question={t("removeQuestion", { scope: scopeName(r.orgUnitId) })}
            confirmLabel={t("removeConfirm")}
            cancelLabel={t("cancel")}
            onConfirm={async () => {
              if (await send(`/api/v1/experience/page-routes/${encodeURIComponent(r.id)}`, "DELETE", null, t("removed"))) await reload();
            }}
          />
        </div>
      ),
    },
  ];

  return (
    <div className="builder-manage" data-testid="approval-routes">
      <DataTable
        caption={t("caption")}
        columns={columns}
        rows={routes}
        rowKey={(r) => r.id}
        emptyTitle={t("emptyTitle")}
        emptyMessage={t("empty")}
        density="compact"
      />
      <form className="builder-manage-form" aria-labelledby={`${scopeId}-title`} onSubmit={(e) => void submit(e)} data-testid="approval-route-form">
        <h2 id={`${scopeId}-title`}>{editing ? t("editTitle", { scope: scopeName(editing.orgUnitId) }) : t("addTitle")}</h2>
        <div className="field">
          <label htmlFor={scopeId}>{t("scope")}</label>
          <select id={scopeId} value={unit} disabled={editing !== null} onChange={(e) => setUnit(e.target.value)}>
            <option value="">{t("wholeTenant")}</option>
            {units.map((u) => (
              <option key={u.id} value={u.id}>
                {unitName(u, locale)}
              </option>
            ))}
          </select>
          <p className="field-hint">{t("scopeHint")}</p>
        </div>
        <fieldset className="builder-manage-people">
          <legend>{t("approvers")}</legend>
          {approvers.length === 0 ? <p className="field-hint">{t("noApprovers")}</p> : null}
          <ul className="builder-manage-list">
            {approvers.map((id) => (
              <li key={id}>
                <PersonPicker label={t("approver")} value={id} readOnly onChange={() => undefined} />
                <Button variant="quiet" size="sm" onClick={() => setApprovers(approvers.filter((x) => x !== id))}>
                  {t("removeApprover")}
                </Button>
              </li>
            ))}
          </ul>
          <PersonPicker label={t("addApprover")} value={null} onChange={(id) => id && setApprovers([...new Set([...approvers, id])])} />
        </fieldset>
        <div className="button-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
            {editing ? t("save") : t("add")}
          </Button>
          {editing ? (
            <Button variant="secondary" onClick={reset}>
              {t("cancel")}
            </Button>
          ) : null}
        </div>
      </form>
      <SendMessages result={result} />
    </div>
  );
}
