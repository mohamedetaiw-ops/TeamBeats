"use client";

import Link from "next/link";
import { useEffect, useId, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { categoryName, expenseTone, type ExpenseClaim, type ExpenseForm, type ReceiptReading } from "@/lib/expenses";
import "./expenses.css";

type Draft = {
  key: number;
  category: string;
  date: string;
  amount: string;
  currency: string;
  merchant: string;
  tax: string;
  days: string;
  km: string;
  justification: string;
  receipt: { id: string; fileName: string } | null;
  reading: ReceiptReading | null;
};

const today = () => new Date().toISOString().slice(0, 10);

/**
 * Sending an expense claim (spec 022 US8): lines with receipts, per diem or mileage, in any currency. The API checks
 * every line against the policy window and the limit for the person's grade, converts foreign amounts at finance's
 * rate and flags duplicates; the page only shows what the API says.
 */
export function MyExpenses({ form }: { form: ExpenseForm }) {
  const t = useTranslations("expenses");
  const format = useFormatter();
  const locale = useLocale();
  const baseId = useId();
  const first = form.categories[0]?.key ?? "";
  const blank = (key: number): Draft => ({
    key,
    category: first,
    date: today(),
    amount: "",
    currency: form.currency ?? "",
    merchant: "",
    tax: "",
    days: "",
    km: "",
    justification: "",
    receipt: null,
    reading: null,
  });
  const [title, setTitle] = useState("");
  const [costCentre, setCostCentre] = useState("");
  const [project, setProject] = useState("");
  const [lines, setLines] = useState<Draft[]>(() => [blank(1)]);
  const [next, setNext] = useState(2);
  const [list, setList] = useState<ExpenseClaim[] | null>(null);
  const [reload, setReload] = useState(0);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [readingKey, setReadingKey] = useState<number | null>(null);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/me/expenses", { cache: "no-store" });
        if (live && response.ok) setList((await response.json()) as ExpenseClaim[]);
      } catch {
        if (live) setList([]);
      }
    })();
    return () => {
      live = false;
    };
  }, [reload]);

  const money = (n: number, currency: string) => format.number(n, { style: "currency", currency });
  const change = (key: number, patch: Partial<Draft>) => setLines((all) => all.map((l) => (l.key === key ? { ...l, ...patch } : l)));

  async function upload(key: number, files: FileList | null) {
    const file = files?.[0];
    if (!file) return;
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/me/expenses/receipts?name=${encodeURIComponent(file.name)}`, {
        method: "POST",
        headers: { "content-type": file.type || "application/octet-stream" },
        body: file,
      });
      if (response.ok) change(key, { receipt: (await response.json()) as { id: string; fileName: string }, reading: null });
      else setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    }
  }

  // The API reads the receipt and only suggests values; they are copied into the line when the person chooses to.
  async function read(key: number, receiptId: string) {
    setReadingKey(key);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/me/expenses/receipts/${receiptId}:read`, { method: "POST" });
      if (response.ok) change(key, { reading: (await response.json()) as ReceiptReading });
      else change(key, { reading: { outcome: "unavailable", suggestion: null } });
    } catch {
      change(key, { reading: { outcome: "unavailable", suggestion: null } });
    } finally {
      setReadingKey(null);
    }
  }

  function applyReading(l: Draft) {
    const s = l.reading?.suggestion;
    if (!s) return;
    change(l.key, {
      merchant: s.merchant ?? l.merchant,
      date: s.date ?? l.date,
      amount: s.amount != null ? String(s.amount) : l.amount,
      currency: s.currency ?? l.currency,
      tax: s.tax != null ? String(s.tax) : l.tax,
      reading: null,
    });
  }

  const number = (v: string) => (v.trim() === "" ? null : Number(v));

  async function send() {
    setBusy(true);
    setNotice(null);
    setMessages([]);
    try {
      const body = {
        title,
        costCentre: costCentre || null,
        project: project || null,
        lines: lines.map((l) => {
          const kind = form.categories.find((c) => c.key === l.category)?.kind;
          return {
            category: l.category,
            date: l.date,
            amount: kind === "amount" ? number(l.amount) : null,
            currency: kind === "amount" ? l.currency : null,
            merchant: kind === "amount" ? l.merchant || null : null,
            tax: kind === "amount" ? number(l.tax) : null,
            receiptId: kind === "amount" ? (l.receipt?.id ?? null) : null,
            days: kind === "per_diem" ? number(l.days) : null,
            km: kind === "mileage" ? number(l.km) : null,
            justification: l.justification || null,
          };
        }),
      };
      const response = await fetch("/api/v1/me/expenses", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      if (!response.ok) {
        const found = await problemMessages(response);
        setMessages(found.length > 0 ? found : [t("failed")]);
        return;
      }
      const claim = (await response.json()) as ExpenseClaim;
      setNotice(t("sent", { reference: claim.reference }));
      setTitle("");
      setCostCentre("");
      setProject("");
      setLines([blank(next)]);
      setNext((n) => n + 1);
      setReload((n) => n + 1);
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="expenses">
      <section className="expenses-section" aria-labelledby="expenses-new">
        <h2 id="expenses-new">{t("new")}</h2>
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
        {!form.currency ? (
          <p className="field-hint">{t("notConfigured")}</p>
        ) : (
          <>
            <p className="field-hint">{t("intro", { days: form.windowDays, currency: form.currency })}</p>
            <FormField label={t("title")} name="expense-title" value={title} maxLength={200} required onChange={(e) => setTitle(e.target.value)} />
            <div className="ui-control-row">
              {form.costCentres.length > 0 ? (
                <SelectField
                  label={t("costCentre")}
                  name="expense-cost-centre"
                  value={costCentre}
                  onChange={(e) => setCostCentre(e.target.value)}
                  options={[{ value: "", label: t("none") }, ...form.costCentres.map((c) => ({ value: c, label: c }))]}
                />
              ) : (
                <FormField label={t("costCentre")} name="expense-cost-centre" value={costCentre} maxLength={50} hint={t("optional")} onChange={(e) => setCostCentre(e.target.value)} />
              )}
              <FormField label={t("project")} name="expense-project" value={project} maxLength={100} hint={t("optional")} onChange={(e) => setProject(e.target.value)} />
            </div>
            {lines.map((l, i) => {
              const category = form.categories.find((c) => c.key === l.category);
              const fileId = `${baseId}-receipt-${l.key}`;
              return (
                <fieldset className="expenses-line" key={l.key}>
                  <legend>{t("line", { n: i + 1 })}</legend>
                  <div className="ui-control-row">
                    <SelectField
                      label={t("category")}
                      name={`line-${l.key}-category`}
                      value={l.category}
                      onChange={(e) => change(l.key, { category: e.target.value, receipt: null })}
                      options={form.categories.map((c) => ({ value: c.key, label: categoryName(c.names, locale, c.key) }))}
                    />
                    <FormField label={t("date")} name={`line-${l.key}-date`} type="date" value={l.date} max={today()} onChange={(e) => change(l.key, { date: e.target.value })} />
                  </div>
                  {category?.kind === "per_diem" ? (
                    <FormField
                      label={t("days")}
                      name={`line-${l.key}-days`}
                      inputMode="numeric"
                      value={l.days}
                      hint={t("perDay", { amount: money(category.rate ?? 0, form.currency!) })}
                      onChange={(e) => change(l.key, { days: e.target.value })}
                    />
                  ) : category?.kind === "mileage" ? (
                    <FormField
                      label={t("km")}
                      name={`line-${l.key}-km`}
                      inputMode="decimal"
                      value={l.km}
                      hint={t("perKm", { amount: money(category.rate ?? 0, form.currency!) })}
                      onChange={(e) => change(l.key, { km: e.target.value })}
                    />
                  ) : (
                    <>
                      <div className="ui-control-row">
                        <FormField label={t("amount")} name={`line-${l.key}-amount`} inputMode="decimal" dir="ltr" value={l.amount} onChange={(e) => change(l.key, { amount: e.target.value })} />
                        <FormField
                          label={t("currency")}
                          name={`line-${l.key}-currency`}
                          dir="ltr"
                          maxLength={3}
                          value={l.currency}
                          hint={t("currencyHint")}
                          onChange={(e) => change(l.key, { currency: e.target.value.toUpperCase() })}
                        />
                        <FormField label={t("tax")} name={`line-${l.key}-tax`} inputMode="decimal" dir="ltr" value={l.tax} hint={t("optional")} onChange={(e) => change(l.key, { tax: e.target.value })} />
                      </div>
                      <FormField label={t("merchant")} name={`line-${l.key}-merchant`} value={l.merchant} maxLength={200} hint={t("optional")} onChange={(e) => change(l.key, { merchant: e.target.value })} />
                      <div className="field">
                        <label htmlFor={fileId}>{t("receipt")}</label>
                        <input id={fileId} type="file" accept="application/pdf,image/png,image/jpeg" onChange={(e) => void upload(l.key, e.target.files)} />
                        <p className="field-hint">
                          {l.receipt ? t("receiptChosen", { name: l.receipt.fileName }) : category?.receipt ? t("receiptNeeded") : t("optional")}
                        </p>
                      </div>
                      {form.receiptReading && l.receipt && !l.reading ? (
                        <div className="ui-control-row">
                          <Button busy={readingKey === l.key} busyLabel={t("reading")} onClick={() => void read(l.key, l.receipt!.id)}>
                            {t("readReceipt")}
                          </Button>
                        </div>
                      ) : null}
                      {l.reading?.outcome === "suggested" && l.reading.suggestion ? (
                        <div className="expenses-reading" role="status">
                          <p className="expenses-reading-title">{t("suggestedTitle")}</p>
                          <dl className="expenses-facts">
                            {l.reading.suggestion.merchant ? (
                              <div>
                                <dt>{t("merchant")}</dt>
                                <dd>
                                  <bdi>{l.reading.suggestion.merchant}</bdi>
                                </dd>
                              </div>
                            ) : null}
                            {l.reading.suggestion.date ? (
                              <div>
                                <dt>{t("date")}</dt>
                                <dd>{format.dateTime(new Date(`${l.reading.suggestion.date}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" })}</dd>
                              </div>
                            ) : null}
                            {l.reading.suggestion.amount != null ? (
                              <div>
                                <dt>{t("amount")}</dt>
                                <dd>
                                  <bdi dir="ltr">
                                    {format.number(l.reading.suggestion.amount)} {l.reading.suggestion.currency ?? ""}
                                  </bdi>
                                </dd>
                              </div>
                            ) : null}
                            {l.reading.suggestion.tax != null ? (
                              <div>
                                <dt>{t("tax")}</dt>
                                <dd>{format.number(l.reading.suggestion.tax)}</dd>
                              </div>
                            ) : null}
                          </dl>
                          <p className="field-hint">{t("suggestedHint")}</p>
                          <div className="ui-control-row">
                            <Button variant="primary" onClick={() => applyReading(l)}>
                              {t("useValues")}
                            </Button>
                            <Button onClick={() => change(l.key, { reading: null })}>{t("dismiss")}</Button>
                          </div>
                        </div>
                      ) : l.reading ? (
                        <p className="field-hint" role="status">
                          {l.reading.outcome === "policy_blocked" ? t("readBlocked") : l.reading.outcome === "nothing_read" ? t("nothingRead") : t("readUnavailable")}
                        </p>
                      ) : null}
                    </>
                  )}
                  {category?.limit != null ? <p className="field-hint">{t("limit", { amount: money(category.limit, form.currency!) })}</p> : null}
                  <TextAreaField
                    label={t("justification")}
                    name={`line-${l.key}-justification`}
                    value={l.justification}
                    maxLength={500}
                    rows={2}
                    hint={t("justificationHint")}
                    onChange={(e) => change(l.key, { justification: e.target.value })}
                  />
                  {lines.length > 1 ? (
                    <div className="ui-control-row">
                      <Button onClick={() => setLines((all) => all.filter((x) => x.key !== l.key))}>{t("removeLine", { n: i + 1 })}</Button>
                    </div>
                  ) : null}
                </fieldset>
              );
            })}
            <div className="ui-control-row">
              <Button
                disabled={lines.length >= 50}
                onClick={() => {
                  setLines((all) => [...all, blank(next)]);
                  setNext((n) => n + 1);
                }}
              >
                {t("addLine")}
              </Button>
              <Button variant="primary" busy={busy} busyLabel={t("sending")} disabled={!title.trim()} onClick={() => void send()}>
                {t("send")}
              </Button>
            </div>
          </>
        )}
      </section>

      <section className="expenses-section" aria-labelledby="expenses-mine">
        <h2 id="expenses-mine">{t("mine")}</h2>
        {list && list.length > 0 ? (
          <ul className="expenses-list">
            {list.map((c) => (
              <li key={c.id}>
                <Link href={`/expenses/${c.id}`}>
                  <bdi dir="ltr">{c.reference}</bdi>
                </Link>
                <span>{c.title}</span>
                <span className="field-hint">{money(c.total, c.currency)}</span>
                <Badge tone={expenseTone[c.status]}>{t(`statuses.${c.status}`)}</Badge>
              </li>
            ))}
          </ul>
        ) : (
          <p className="field-hint">{t("noClaims")}</p>
        )}
      </section>
    </div>
  );
}
