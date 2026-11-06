"use client";

import { useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";

/** Reference pack beta: a block that only says it is there (registry rule tests). Contract test component only. */
export default function ReferenceRestricted(_: PlacementProps) {
  const t = useTranslations("experience.reference");
  return <p className="ref-card">{t("restricted")}</p>;
}
