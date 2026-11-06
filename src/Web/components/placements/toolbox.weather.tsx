"use client";

import { useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";
import "@/components/experience/toolbox.css";
import { StateView } from "@/components/ui/StateView";

/**
 * Tool box weather (spec 008 FR-050). Weather comes through a backend connector once a provider is chosen (open
 * decision); until then the component shows the shared unsupported state instead of guessing.
 */
export default function Weather(_: PlacementProps) {
  const t = useTranslations("toolbox.weather");
  return (
    <section className="toolbox" aria-label={t("title")}>
      <h2 className="toolbox-title">{t("title")}</h2>
      <StateView kind="unsupported" title={t("unsupported")} message="" />
    </section>
  );
}
