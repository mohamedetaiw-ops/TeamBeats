"use client";

import Link from "next/link";
import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, TextAreaField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { categoryName, expenseTone, type ExpenseClaim, type ExpensePolicyView, type ExpenseRate } from "@/lib/expenses";
import "./expenses.css";

/**
 * The expense policy, exchange rates and the claims finance covers (spec 022 FR-023). The policy is edited as JSON for
 * now, with the API validating every field; only finance officers of the whole organization change it.
 */
export function ExpenseAdmin({ policy: initial, rates: initialRates, claims }: { policy: ExpensePolicyView; rates: ExpenseRate[]; claims: ExpenseClaim[] }) {
  const t = useTranslations("expenses");
  const ta = useTranslations("expenses.admin");
  const format = useFormatter();
  const locale = useLocale();
  const [view, setView] = useState(initial);
  const [text, setText] = useState(() => JSON.stringify(initial.policy, null, 2));
  const [rates, setRates] = useState(initialRates);
  const [rate, setRate] = useState({ currency: "", date: new Date().toISOString().slice(0, 10), rate: "" });
  const [busy, setBusy] = useState<"policy" | "rate" | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function call(kind: "policy" | "rate", run: () => Promise<Response>, done: (response: Response) => Promise<void>) {
    setBusy(kind);
    setNotice(null);
    setMessages([]);
    try {
      const response = await run();
      if (!response.ok) {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [ta("failed")]);
        return;
      }
      await done(response);
    } catch {
      setMessages([ta("failed")]);
    } finally {
      setBusy(null);
    }
  }

  function savePolicy() {
    let body: unknown;
    try {
      body = JSON.parse(text);
    } catch {
      setMessages([ta("notJson")]);
      return;
    }
    void call(
      "policy",
      () => fetch("/api/v1/hr/expense-policy", { method: "PUT", headers: { "content-type": "application/json", "if-match": view.eTag }, body: JSON.stringify(body) }),
      async (response) => {
        const saved = (await response.json()) as ExpensePolicyView;
        setView(saved);
        setText(JSON.stringify(saved.policy, null, 2));
        setNotice(ta("saved"));
      },
    );
  }

  function addRate() {
    void call(
      "rate",
      () =>
        fetch("/api/v1/hr/expense-rates", {
          method: "PUT",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ rates: [{ currency: rate.currency, date: rate.date, rate: Number(rate.rate) }] }),
        }),
      async (response) => {
        setRates((await response.json()) as ExpenseRate[]);
        setRate((r) => ({ ...r, currency: "", rate: "" }));
        setNotice(ta("rateSaved"));
      },
    );
  }

  const money = (n: number, currency: string) => format.number(n, { style: "currency", currency });

  return (
    <div className="expenses">
      {notice ? (
        <p className="notice" role="status">
          {notice}
        </p>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={ta("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <section className="expenses-section" aria-labelledby="expenses-policy">
        <h2 id="expenses-policy">{ta("policy")}</h2>
        {!view.canChange ? <p className="field-hint">{ta("wholeOrganizationOnly")}</p> : null}
        <dl className="expenses-facts">
          <div>
            <dt>{ta("currency")}</dt>
            <dd>{view.policy.currency ?? ta("notSet")}</dd>
          </div>
          <div>
            <dt>{ta("window")}</dt>
            <dd>{ta("days", { days: view.policy.windowDays })}</dd>
          </div>
          <div>
            <dt>{ta("categories")}</dt>
            <dd>{view.policy.categories.map((c) => categoryName(c.names, locale, c.key)).join(", ")}</dd>
          </div>
          <div>
            <dt>{ta("tiers")}</dt>
            <dd>{view.policy.tiers.length === 0 ? ta("noTiers") : view.policy.tiers.map((x) => format.number(x.above)).join(", ")}</dd>
          </div>
        </dl>
        {view.canChange ? (
          <>
            <TextAreaField label={ta("policyJson")} name="expense-policy" dir="ltr" rows={14} value={text} hint={ta("policyHint")} onChange={(e) => setText(e.target.value)} />
            <div className="ui-control-row">
              <Button variant="primary" busy={busy === "policy"} busyLabel={ta("saving")} onClick={savePolicy}>
                {ta("save")}
              </Button>
            </div>
          </>
        ) : null}
      </section>

      <section className="expenses-section" aria-labelledby="expenses-rates">
        <h2 id="expenses-rates">{ta("rates")}</h2>
        <p className="field-hint">{ta("ratesIntro", { currency: view.policy.currency ?? "" })}</p>
        {view.canChange ? (
          <div className="ui-control-row">
            <FormField label={t("currency")} name="rate-currency" dir="ltr" maxLength={3} value={rate.currency} onChange={(e) => setRate((r) => ({ ...r, currency: e.target.value.toUpperCase() }))} />
            <FormField label={ta("from")} name="rate-date" type="date" value={rate.date} onChange={(e) => setRate((r) => ({ ...r, date: e.target.value }))} />
            <FormField label={ta("rate")} name="rate-value" inputMode="decimal" dir="ltr" value={rate.rate} onChange={(e) => setRate((r) => ({ ...r, rate: e.target.value }))} />
            <Button busy={busy === "rate"} busyLabel={ta("saving")} disabled={!rate.currency || !rate.rate} onClick={addRate}>
              {ta("addRate")}
            </Button>
          </div>
        ) : null}
        <DataTable<ExpenseRate>
          caption={ta("rates")}
          captionHidden
          rows={rates}
          rowKey={(r) => `${r.currency}-${r.date}`}
          density="compact"
          emptyTitle={ta("noRates")}
          columns={[
            { key: "currency", header: t("currency"), rowHeader: true, cell: (r) => <bdi dir="ltr">{r.currency}</bdi> },
            { key: "date", header: ta("from"), cell: (r) => format.dateTime(new Date(`${r.date}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }) },
            { key: "rate", header: ta("rate"), cell: (r) => format.number(r.rate, { maximumFractionDigits: 8 }) },
          ]}
        />
      </section>

      <section className="expenses-section" aria-labelledby="expenses-claims">
        <h2 id="expenses-claims">{ta("claims")}</h2>
        <DataTable<ExpenseClaim>
          caption={ta("caption")}
          captionHidden
          rows={claims}
          rowKey={(c) => c.id}
          density="compact"
          emptyTitle={ta("empty")}
          columns={[
            {
              key: "reference",
              header: t("detail.reference"),
              rowHeader: true,
              cell: (c) => (
                <Link href={`/expenses/${c.id}`}>
                  <bdi dir="ltr">{c.reference}</bdi>
                </Link>
              ),
            },
            { key: "person", header: t("detail.person"), sortValue: (c) => c.person.displayName, cell: (c) => <bdi>{c.person.displayName}</bdi> },
            { key: "total", header: t("detail.total"), sortValue: (c) => c.total, cell: (c) => money(c.total, c.currency) },
            { key: "flags", header: t("detail.checks"), cell: (c) => (c.duplicate ? <Badge tone="danger">{t("detail.duplicateLine")}</Badge> : null) },
            { key: "status", header: t("detail.status"), cell: (c) => <Badge tone={expenseTone[c.status]}>{t(`statuses.${c.status}`)}</Badge> },
          ]}
        />
      </section>
    </div>
  );
}
