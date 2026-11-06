"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";

/** Reference pack beta: a counter that needs a capability, so packages without it see it as unavailable. */
export default function Counter(_: PlacementProps) {
  const t = useTranslations("experience.reference");
  const [count, setCount] = useState(0);
  return (
    <button type="button" className="button" onClick={() => setCount((c) => c + 1)}>
      {t("counter", { count })}
    </button>
  );
}
