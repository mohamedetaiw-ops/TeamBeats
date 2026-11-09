import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { versionHref, type VersionHistoryItem } from "@/lib/policies/types";
import "./policies.css";

/** Version history for the owner and compliance officers: each number, whether it was material, dates and approvers. */
export function VersionList({ policyId, versions }: { policyId: string; versions: VersionHistoryItem[] }) {
  const t = useTranslations("policies.history");
  const ts = useTranslations("policies.versionState");
  const format = useFormatter();
  const date = (iso: string | null) => (iso ? format.dateTime(new Date(iso), { dateStyle: "medium" }) : "");
  return (
    <section aria-labelledby="policy-history" className="policies-card">
      <h2 id="policy-history">{t("title")}</h2>
      <div className="policies-table">
        <table data-testid="policy-versions">
          <thead>
            <tr>
              <th scope="col">{t("number")}</th>
              <th scope="col">{t("state")}</th>
              <th scope="col">{t("change")}</th>
              <th scope="col">{t("effective")}</th>
              <th scope="col">{t("approvedBy")}</th>
            </tr>
          </thead>
          <tbody>
            {versions.map((v) => (
              <tr key={v.id}>
                <td>
                  <Link href={versionHref(policyId, v.number)}>{t("version", { number: v.number })}</Link>
                </td>
                <td>{ts(v.state)}</td>
                <td>
                  {t(v.material ? "material" : "minor")}
                  {v.changeNote ? (
                    <>
                      {" · "}
                      <bdi>{v.changeNote}</bdi>
                    </>
                  ) : null}
                </td>
                <td>{date(v.effectiveFrom)}</td>
                <td>
                  {v.approvals.map((a, i) => (
                    <span key={`${a.approver.id}-${a.at}`}>
                      {i > 0 ? ", " : null}
                      <bdi>{a.approver.displayName}</bdi>
                    </span>
                  ))}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
