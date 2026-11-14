"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { TaskDecisionPanel } from "@/features/services/TaskDecisionPanel";
import { problemMessages } from "@/lib/notifications/types";
import { expenseTone, type ExpenseClaim, type ExpenseLine } from "@/lib/expenses";
import "./expenses.css";

/**
 * One expense claim (spec 022 US8). The approver sees each line in both currencies, the limit and the duplicate flags;
 * once finance approves, the finance reference and the payment status show. The API checks every action again.
 */
export function ExpenseDetail({ initial, taskId, names }: { initial: ExpenseClaim; taskId: string | null; names: Record<string, string> }) {
  const t = useTranslations("expenses");
  const format = useFormatter();
  const [claim, setClaim] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const money = (n: number, currency: string) => format.number(n, { style: "currency", currency });

  async function withdraw() {
    setBusy(true);
    setNotice(null);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/hr/expenses/${claim.id}:withdraw`, { method: "POST" });
      if (response.ok) {
        setClaim((await response.json()) as ExpenseClaim);
        setNotice(t("detail.withdrawn"));
      } else {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [t("failed")]);
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  async function reload() {
    const response = await fetch(`/api/v1/hr/expenses/${claim.id}`, { cache: "no-store" });
    if (response.ok) setClaim((await response.json()) as ExpenseClaim);
    setNotice(t("detail.decided"));
  }

  return (
    <div className="expenses">
      <section className="expenses-section" aria-labelledby="expense-summary">
        <h2 id="expense-summary">
          <bdi>{claim.title}</bdi>
        </h2>
        {notice ? (
          <p className="notice" role="status">
            {notice}
          </p>
        ) : null}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {claim.duplicate ? (
          <Alert tone="warning" title={t("detail.duplicateTitle")}>
            {t("detail.duplicate")}
          </Alert>
        ) : null}
        <dl className="expenses-facts">
          <div>
            <dt>{t("detail.reference")}</dt>
            <dd>
              <bdi dir="ltr">{claim.reference}</bdi>
            </dd>
          </div>
          <div>
            <dt>{t("detail.person")}</dt>
            <dd>
              <bdi>{claim.person.displayName}</bdi>
            </dd>
          </div>
          <div>
            <dt>{t("detail.status")}</dt>
            <dd>
              <Badge tone={expenseTone[claim.status]}>{t(`statuses.${claim.status}`)}</Badge>
            </dd>
          </div>
          <div>
            <dt>{t("detail.total")}</dt>
            <dd>{money(claim.total, claim.currency)}</dd>
          </div>
          <div>
            <dt>{t("detail.sent")}</dt>
            <dd>{format.dateTime(new Date(claim.createdAt), { dateStyle: "medium" })}</dd>
          </div>
          {claim.costCentre ? (
            <div>
              <dt>{t("costCentre")}</dt>
              <dd>
                <bdi>{claim.costCentre}</bdi>
              </dd>
            </div>
          ) : null}
          {claim.project ? (
            <div>
              <dt>{t("project")}</dt>
              <dd>
                <bdi>{claim.project}</bdi>
              </dd>
            </div>
          ) : null}
          {claim.externalReference ? (
            <div>
              <dt>{t("detail.financeReference")}</dt>
              <dd>
                <bdi dir="ltr">{claim.externalReference}</bdi>
              </dd>
            </div>
          ) : null}
          {claim.paymentStatus ? (
            <div>
              <dt>{t("detail.payment")}</dt>
              <dd>{t(`payments.${claim.paymentStatus}`)}</dd>
            </div>
          ) : null}
        </dl>
        {claim.status === "approved" && claim.financeState === "unavailable" ? <p className="field-hint">{t("detail.outside")}</p> : null}
        {claim.status === "approved" && claim.financeState !== "unavailable" ? <p className="field-hint">{t("detail.sending")}</p> : null}
        {claim.status === "handoff_failed" ? <p className="field-hint">{t("detail.handoffFailed")}</p> : null}
        <DataTable<ExpenseLine>
          caption={t("detail.lines")}
          rows={claim.lines}
          rowKey={(l) => String(l.n)}
          density="compact"
          emptyTitle={t("detail.lines")}
          columns={[
            { key: "category", header: t("category"), rowHeader: true, cell: (l) => names[l.category] ?? l.category },
            { key: "date", header: t("date"), cell: (l) => format.dateTime(new Date(`${l.date}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }) },
            {
              key: "amount",
              header: t("amount"),
              cell: (l) => (
                <>
                  {money(l.baseAmount, claim.currency)}
                  {l.currency !== claim.currency ? <span className="field-hint"> {t("detail.original", { amount: money(l.amount, l.currency) })}</span> : null}
                  {l.days ? <span className="field-hint"> {t("detail.days", { days: l.days })}</span> : null}
                  {l.km ? <span className="field-hint"> {t("detail.km", { km: format.number(l.km) })}</span> : null}
                </>
              ),
            },
            { key: "merchant", header: t("merchant"), cell: (l) => (l.merchant ? <bdi>{l.merchant}</bdi> : null) },
            {
              key: "checks",
              header: t("detail.checks"),
              cell: (l) => (
                <span className="expenses-flags">
                  {l.overLimit ? <Badge tone="warning">{t("detail.overLimit", { amount: money(l.limit ?? 0, claim.currency) })}</Badge> : null}
                  {l.duplicate ? <Badge tone="danger">{t("detail.duplicateLine")}</Badge> : null}
                  {l.justification ? <span className="expenses-justification">{l.justification}</span> : null}
                </span>
              ),
            },
            {
              key: "receipt",
              header: t("receipt"),
              cell: (l) =>
                l.receiptId ? (
                  <a className="expenses-link" href={`/api/v1/hr/expenses/${claim.id}/receipts/${l.receiptId}`} download>
                    {t("detail.download", { name: l.receiptName ?? "" })}
                  </a>
                ) : null,
            },
          ]}
        />
        {claim.rights.withdraw ? (
          <div className="ui-control-row">
            <Button busy={busy} busyLabel={t("sending")} onClick={() => void withdraw()}>
              {t("detail.withdraw")}
            </Button>
          </div>
        ) : null}
      </section>
      {taskId && claim.rights.decide ? <TaskDecisionPanel taskId={taskId} title={t("detail.decide")} onDone={() => void reload()} /> : null}
    </div>
  );
}
