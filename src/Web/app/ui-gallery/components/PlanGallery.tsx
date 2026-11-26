"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { PlanMap, type PlanPin } from "@/components/ui/PlanMap";

const pins = [
  { key: "d1", x: 18, y: 21, state: "free" },
  { key: "d2", x: 50, y: 18, state: "busy" },
  { key: "d3", x: 50, y: 38, state: "unavailable" },
  { key: "r1", x: 82, y: 50, state: "free" },
] as const;

/**
 * The floor plan map (spec 031 T012): places free, taken and not bookable, and the one picked. It comes last on the
 * page because the scaled plan image has a fractional height, which would move every section after it by a pixel.
 */
export function PlanGallery() {
  const t = useTranslations("components_gallery");
  const [pin, setPin] = useState<string | null>(null);
  return (
    <section aria-labelledby="cg-plan">
      <h2 id="cg-plan">{t("plan.title")}</h2>
      <p className="notice">{t("plan.intro")}</p>
      <div className="gallery-wide">
        <PlanMap label={t("plan.label")} src="/design-system/floor-plan.svg" alt={t("plan.alt")} width={800} height={500} onSelect={setPin}
          pins={pins.map((p): PlanPin => ({ ...p, label: t(`plan.${p.key}`), state: pin === p.key ? "selected" : p.state }))} />
      </div>
    </section>
  );
}
