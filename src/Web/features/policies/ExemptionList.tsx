"use client";

import Link from "next/link";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { exemptionsHref, nameIn, type ExemptionStatus, type ExemptionView } from "@/lib/policies/types";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import "./policies.css";

const tones: Record<ExemptionStatus, BadgeTone> = { pending: "info", approved: "success", rejected: "warning", withdrawn: "neutral", expired: "neutral" };

/**
 * Exemptions as a table (spec 025 US7): who or which unit, the reason, the end date, the status and who decided. On the
 * register it names the policy. A pending request can be withdrawn where the API allows it.
 */
export function ExemptionList({ items, showPolicy = false, focus = null }: { items: ExemptionView[]; showPolicy?: boolean; focus?: string | null }) {
  const t = useTranslations("policies.exemptions");
  const format = useFormatter();
  const locale = useLocale();
  const router = useRouter();
  const [messages, setMessages] = useState<string[]>([]);
  const day = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium" });

  async function withdraw(e: ExemptionView) {
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(e.policy.id)}/exemptions/${encodeURIComponent(e.id)}/withdraw`, { method: "POST" });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      router.refresh();
    } catch {
      setMessages([t("failed")]);
    }
  }

  return (
    <>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <div className="policies-table">
        <table data-testid="exemption-list" data-stack>
          <thead>
            <tr>
              {showPolicy ? <th scope="col">{t("policy")}</th> : null}
              <th scope="col">{t("who")}</th>
              <th scope="col">{t("reason")}</th>
              <th scope="col">{t("ends")}</th>
              <th scope="col">{t("status")}</th>
              <th scope="col">{t("decision")}</th>
            </tr>
          </thead>
          <tbody>
            {items.map((e) => (
              <tr key={e.id} aria-current={focus === e.id ? "true" : undefined} data-testid="exemption-row">
                {showPolicy ? (
                  <td data-label={t("policy")}>
                    <Link href={`${exemptionsHref(e.policy.id)}?exemption=${encodeURIComponent(e.id)}`}>
                      <bdi>{t("policyName", { code: e.policy.code, name: nameIn(e.policy.names, locale) })}</bdi>
                    </Link>
                  </td>
                ) : null}
                <th scope="row" data-label={t("who")}>
                  {e.scope === "unit" ? (
                    <bdi>{t("unitScope", { unit: e.unitNames ? nameIn(e.unitNames, locale) : "" })}</bdi>
                  ) : (
                    <bdi>{e.person?.displayName ?? ""}</bdi>
                  )}
                  <span className="policies-meta">
                    {" "}
                    {t("askedBy", { name: e.requestedBy.displayName, date: day(e.requestedAt) })}
                  </span>
                </th>
                <td data-label={t("reason")}>
                  <bdi>{e.reason}</bdi>
                </td>
                <td data-label={t("ends")}>{day(e.endsOn)}</td>
                <td data-label={t("status")}>
                  <Badge tone={tones[e.status]}>{t(`statuses.${e.status}`)}</Badge>
                </td>
                <td data-label={t("decision")}>
                  {e.decidedBy && e.decidedAt ? <bdi>{t("decidedBy", { name: e.decidedBy.displayName, date: day(e.decidedAt) })}</bdi> : t("noDecision")}
                  {e.canWithdraw ? (
                    <ConfirmAction label={t("withdraw")} question={t("withdrawQuestion")} confirmLabel={t("withdrawConfirm")} cancelLabel={t("cancel")} onConfirm={() => withdraw(e)} />
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
