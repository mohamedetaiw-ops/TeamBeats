"use client";

import { useFormatter, useTranslations } from "next-intl";
import { FINANCE_STATES, FINANCE_STATUSES, known, type FinanceHandOff } from "@/lib/integrations/types";
import { ErrorText } from "./parts";

/**
 * Claims handed to finance through this connection (spec 017 US7 scenario 2): the finance reference, the payment status
 * last read and when. No claim content is shown here; the claim stays in its own module.
 */
export function FinanceHandOffsPanel({ items }: { items: FinanceHandOff[] | null }) {
  const t = useTranslations("connections.finance.handoffs");
  const format = useFormatter();
  const date = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" }) : t("never"));
  return (
    <section className="connections-section" aria-labelledby="finance-handoffs-heading">
      <h2 id="finance-handoffs-heading">{t("title")}</h2>
      <p>{t("intro")}</p>
      {items === null ? (
        <p className="notice">{t("unavailable")}</p>
      ) : items.length === 0 ? (
        <p className="notice">{t("empty")}</p>
      ) : (
        <div className="connections-table">
          <table data-testid="finance-handoffs">
            <thead>
              <tr>
                <th scope="col">{t("created")}</th>
                <th scope="col">{t("state")}</th>
                <th scope="col">{t("reference")}</th>
                <th scope="col">{t("payment")}</th>
                <th scope="col">{t("checked")}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((h) => (
                <tr key={h.id}>
                  <td>{date(h.createdAt)}</td>
                  <td>
                    {known(FINANCE_STATES, h.state) ? t(`states.${h.state}`) : h.state}
                    {h.errorClass ? (
                      <>
                        {" "}
                        (<ErrorText errorClass={h.errorClass} />)
                      </>
                    ) : null}
                  </td>
                  <td>{h.externalReference ? <bdi dir="ltr">{h.externalReference}</bdi> : t("none")}</td>
                  <td>{h.paymentStatus && known(FINANCE_STATUSES, h.paymentStatus) ? t(`payments.${h.paymentStatus}`) : t("none")}</td>
                  <td>{date(h.checkedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
