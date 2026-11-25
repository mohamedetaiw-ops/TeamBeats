"use client";

import { useEffect, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { StateView } from "@/components/ui/StateView";
import type { CompensationComponent, CompensationRead } from "@/lib/compensation";
import { problemMessages } from "@/lib/notifications/types";
import { stepUpIfAsked, type StepUpProblem } from "@/lib/security/types";

type View = { kind: "loading" } | { kind: "stepUp" } | { kind: "error"; messages: string[] } | { kind: "read"; read: CompensationRead };

const ENDPOINT = "/api/v1/me/compensation";

/**
 * My compensation (spec 019 R3 US10, FR-023): the signed-in person's own current pay components, after a fresh sign-in
 * (step-up). The statement is read live each time; when HR does not answer, nothing is shown. The total comes from the
 * API, which gives one only when the components agree on currency and frequency. Nothing here is kept in the browser.
 */
export function MyCompensation() {
  const t = useTranslations("compensation");
  const format = useFormatter();
  const [view, setView] = useState<View>({ kind: "loading" });

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch(ENDPOINT, { cache: "no-store" });
        if (!live) return;
        if (response.ok) return setView({ kind: "read", read: (await response.json()) as CompensationRead });
        if (response.status === 401) {
          const problem = (await response.clone().json().catch(() => null)) as Partial<StepUpProblem> | null;
          if (problem?.code === "security.step_up_required") return setView({ kind: "stepUp" });
        }
        const messages = await problemMessages(response);
        setView({ kind: "error", messages: messages.length > 0 ? messages : [t("failed")] });
      } catch {
        if (live) setView({ kind: "error", messages: [t("failed")] });
      }
    })();
    return () => {
      live = false;
    };
  }, [t]);

  async function stepUp() {
    const response = await fetch(ENDPOINT, { cache: "no-store" });
    if (!(await stepUpIfAsked(response, "/compensation")) && response.ok) setView({ kind: "read", read: (await response.json()) as CompensationRead });
  }

  const money = (value: number, currency: string | null) =>
    currency ? format.number(value, { style: "currency", currency }) : format.number(value, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const day = (iso: string | null) => (iso ? format.dateTime(new Date(`${iso}T00:00:00Z`), { dateStyle: "long", timeZone: "UTC" }) : t("none"));

  if (view.kind === "loading") return <StateView kind="loading" />;
  if (view.kind === "stepUp") {
    return <StateView kind="denied" title={t("stepUp.title")} message={t("stepUp.message")} action={<Button onClick={() => void stepUp()}>{t("stepUp.action")}</Button>} />;
  }
  if (view.kind === "error") return <StateView kind="error" message={view.messages.join(" ")} />;
  const { read } = view;
  if (read.state === "unavailable" || !read.statement) {
    if (read.state === "not_matched") return <StateView kind="empty" title={t("notMatched.title")} message={t("notMatched.message")} />;
    return <StateView kind="error" title={t("unavailable.title")} message={t("unavailable.message")} />;
  }
  const statement = read.statement;

  const columns: Column<CompensationComponent>[] = [
    { key: "name", header: t("component"), cell: (c) => <bdi>{c.name ?? c.code}</bdi>, rowHeader: true },
    { key: "frequency", header: t("frequency"), cell: (c) => (c.frequency ? <bdi>{c.frequency}</bdi> : t("none")) },
    { key: "from", header: t("from"), cell: (c) => day(c.startDate) },
    { key: "amount", header: t("amount"), cell: (c) => money(c.amount, c.currency), align: "end" },
  ];
  return (
    <>
      <p className="field-hint">{t("privacy")}</p>
      <DataTable
        caption={t("caption")}
        columns={columns}
        rows={statement.components}
        rowKey={(c) => c.code}
        emptyTitle={t("empty.title")}
        emptyMessage={t("empty.message")}
      />
      {statement.total !== null ? (
        <p data-testid="compensation-total">
          <strong>{t("total", { amount: money(statement.total, statement.currency) })}</strong>
        </p>
      ) : statement.components.length > 0 ? (
        <p className="field-hint">{t("noTotal")}</p>
      ) : null}
      {statement.effectiveDate ? <p className="field-hint">{t("effective", { date: day(statement.effectiveDate) })}</p> : null}
    </>
  );
}
