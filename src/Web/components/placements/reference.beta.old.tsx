"use client";

import { useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";

/** Reference pack beta: a block that only says it is there (registry rule tests). Contract test component only. */
export default function ReferenceOld(_: PlacementProps) {
  const t = useTranslations("experience.reference");
  return <p className="ref-card">{t("old")}</p>;
}
