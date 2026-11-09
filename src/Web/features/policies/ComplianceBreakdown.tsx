import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import type { ComplianceCounts } from "@/lib/policies/types";
import "./policies.css";

export type BreakdownRow = { label: React.ReactNode; counts: ComplianceCounts; people: string };

/** One breakdown of the compliance view (spec 025 US3 scenario 1): people, accepted, overdue and share per line, with the people behind it. */
export function ComplianceBreakdown({ kind, rows }: { kind: "unit" | "location" | "manager"; rows: BreakdownRow[] }) {
  const t = useTranslations("policies.compliance");
  const format = useFormatter();
  return (
    <section className="policies-card" aria-labelledby={`by-${kind}`}>
      <h2 id={`by-${kind}`}>{t(`by.${kind}`)}</h2>
      <div className="policies-table">
        <table data-testid={`compliance-by-${kind}`}>
          <thead>
            <tr>
              <th scope="col">{t(`column.${kind}`)}</th>
              <th scope="col">{t("column.members")}</th>
              <th scope="col">{t("column.attested")}</th>
              <th scope="col">{t("column.overdue")}</th>
              <th scope="col">{t("column.percent")}</th>
              <th scope="col">
                <span className="visually-hidden">{t("column.people")}</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r, i) => (
              <tr key={i}>
                <th scope="row">{r.label}</th>
                <td>{format.number(r.counts.members)}</td>
                <td>{format.number(r.counts.attested)}</td>
                <td>{format.number(r.counts.overdue)}</td>
                <td>{format.number(r.counts.percent / 100, { style: "percent", maximumFractionDigits: 1 })}</td>
                <td>
                  <Link href={r.people}>{t("seePeople")}</Link>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
