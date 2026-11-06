"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";

/** Reference pack beta: listens for filterChanged from any publisher, without knowing which one. Contract test only. */
export default function ItemList({ props, events }: PlacementProps) {
  const t = useTranslations("experience.reference");
  const [query, setQuery] = useState("");
  useEffect(() => events.subscribe("filterChanged", (payload) => setQuery(typeof payload.query === "string" ? payload.query : "")), [events]);
  const size = typeof props.pageSize === "number" ? props.pageSize : 10;
  return (
    <section className="ref-card" aria-live="polite">
      <p data-filter-query={query}>{query ? t("listFiltered", { query, size }) : t("listAll", { size })}</p>
    </section>
  );
}
