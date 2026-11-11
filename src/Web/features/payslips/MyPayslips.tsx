"use client";

import { useEffect, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { StateView } from "@/components/ui/StateView";
import { problemMessages } from "@/lib/notifications/types";
import type { Payslip, PayslipList } from "@/lib/payslips";
import { stepUpIfAsked, type StepUpProblem } from "@/lib/security/types";

type View = { kind: "loading" } | { kind: "stepUp" } | { kind: "error"; messages: string[] } | { kind: "list"; list: PayslipList };

/**
 * My payslips (spec 019 US6, FR-010 to FR-012): the signed-in person's own payslips, after a fresh sign-in (step-up).
 * The list is read live each time; when payroll does not answer, nothing is shown. The PDF download is made by the API,
 * which stamps it with the person and the time and audits it. Nothing here is kept in the browser.
 */
export function MyPayslips() {
  const t = useTranslations("payslips");
  const format = useFormatter();
  const [view, setView] = useState<View>({ kind: "loading" });

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/me/payslips", { cache: "no-store" });
        if (!live) return;
        if (response.ok) return setView({ kind: "list", list: (await response.json()) as PayslipList });
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
    const response = await fetch("/api/v1/me/payslips", { cache: "no-store" });
    if (!(await stepUpIfAsked(response, "/payslips")) && response.ok) setView({ kind: "list", list: (await response.json()) as PayslipList });
  }

  const month = (period: string) => format.dateTime(new Date(`${period}-01T00:00:00Z`), { year: "numeric", month: "long", timeZone: "UTC" });
  const money = (value: number | null, currency: string | null) =>
    value === null ? t("none") : currency ? format.number(value, { style: "currency", currency }) : format.number(value, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  if (view.kind === "loading") return <StateView kind="loading" />;
  if (view.kind === "stepUp") {
    return <StateView kind="denied" title={t("stepUp.title")} message={t("stepUp.message")} action={<Button onClick={() => void stepUp()}>{t("stepUp.action")}</Button>} />;
  }
  if (view.kind === "error") return <StateView kind="error" message={view.messages.join(" ")} />;
  const { list } = view;
  if (list.state === "unavailable") return <StateView kind="error" title={t("unavailable.title")} message={t("unavailable.message")} />;
  if (list.state === "not_matched") return <StateView kind="empty" title={t("notMatched.title")} message={t("notMatched.message")} />;

  const columns: Column<Payslip>[] = [
    { key: "period", header: t("period"), cell: (p) => month(p.period), rowHeader: true },
    { key: "gross", header: t("gross"), cell: (p) => money(p.gross, p.currency), align: "end" },
    { key: "deductions", header: t("deductions"), cell: (p) => money(p.deductions, p.currency), align: "end" },
    { key: "net", header: t("net"), cell: (p) => <strong>{money(p.net, p.currency)}</strong>, align: "end" },
    ...(list.hasPdf
      ? [
          {
            key: "pdf",
            header: t("pdf"),
            cell: (p: Payslip) => (
              <a href={`/api/v1/me/payslips/${encodeURIComponent(p.period)}/pdf`} download>
                {t("download", { period: month(p.period) })}
              </a>
            ),
          },
        ]
      : []),
  ];
  return (
    <>
      <p className="field-hint">{t("privacy")}</p>
      <DataTable caption={t("caption")} columns={columns} rows={list.payslips} rowKey={(p) => p.period} emptyTitle={t("empty.title")} emptyMessage={t("empty.message")} />
    </>
  );
}
