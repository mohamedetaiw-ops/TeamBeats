"use client";

import { localized, type PlacementProps } from "@/lib/experience/contract";

/** Reference pack alpha: one number with a label, formatted for the viewer's locale. Contract test component only. */
export default function KpiTile({ props, context }: PlacementProps) {
  const value = typeof props.value === "number" ? props.value : 0;
  const percent = props.unit === "percent";
  const formatted = new Intl.NumberFormat(context.locale, percent ? { style: "percent", maximumFractionDigits: 1 } : {}).format(
    percent ? value / 100 : value,
  );
  return (
    <figure className="ref-card ref-kpi">
      <figcaption>{localized(props.label, context.locale)}</figcaption>
      <data className="ref-kpi-value" value={value}>
        {formatted}
      </data>
    </figure>
  );
}
