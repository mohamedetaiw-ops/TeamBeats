"use client";

import { useId, useState } from "react";
import { useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";

/** Reference pack alpha: publishes filterChanged, its only declared event. Contract test component only. */
export default function FilterPublisher({ props, context, events }: PlacementProps) {
  const t = useTranslations("experience.reference");
  const id = useId();
  const [query, setQuery] = useState("");
  return (
    <div className="ref-card">
      <label htmlFor={id}>{t("filterLabel")}</label>
      <input id={id} type="search" value={query} placeholder={localized(props.placeholder, context.locale)} maxLength={100}
        onChange={(e) => {
          setQuery(e.target.value);
          events.publish("filterChanged", { query: e.target.value });
        }} />
    </div>
  );
}
