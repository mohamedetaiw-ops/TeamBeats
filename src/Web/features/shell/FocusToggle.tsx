"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Icon } from "@/components/ui/Icon";

/**
 * Focus or reading mode (spec 008 FR-009): one button in the shell that hides the side rail, menus below the bar and the
 * secondary regions of a page, so the content takes the width. Headings, landmarks and the skip link stay. It applies at
 * once and is saved as a device-specific preference, so each device keeps its own choice.
 */
export function FocusToggle({ initial }: { initial: boolean }) {
  const t = useTranslations("shell.focus");
  const [on, setOn] = useState(initial);
  const [status, setStatus] = useState("");

  function toggle() {
    const next = !on;
    setOn(next);
    if (next) document.documentElement.dataset.focus = "on";
    else delete document.documentElement.dataset.focus;
    setStatus(next ? t("on") : t("off"));
    void fetch("/api/v1/me/preference-profile", {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ values: { focus: next ? "on" : "off" } }),
    }).catch(() => undefined);
  }

  return (
    <>
      <button type="button" className="button button-quiet shell-icon-button shell-focus-toggle" aria-pressed={on} aria-label={t("label")} title={t("label")} onClick={toggle}>
        <Icon name="eye" size={20} />
      </button>
      <span className="visually-hidden" role="status" aria-live="polite">
        {status}
      </span>
    </>
  );
}
