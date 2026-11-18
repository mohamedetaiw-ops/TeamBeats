"use client";

import type { ReactNode } from "react";
import { useTranslations } from "next-intl";

/** An empty table cell (spec 036 DLS-095): a dash people see, "None" for assistive technology. */
export function EmptyCell() {
  const t = useTranslations("ui.data");
  return (
    <span className="ui-empty-cell">
      <span aria-hidden="true">{t("dash")}</span>
      <span className="visually-hidden">{t("none")}</span>
    </span>
  );
}

/** The cell's content, or the dash when there is none. */
export function orDash(value: ReactNode): ReactNode {
  return value === null || value === undefined || value === "" || value === false ? <EmptyCell /> : value;
}
