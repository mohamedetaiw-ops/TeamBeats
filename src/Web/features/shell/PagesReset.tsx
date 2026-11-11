"use client";

import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { ConfirmAction } from "@/components/ui/ConfirmAction";

/** Returns every page the person arranged to the organization's layout, after a second step (spec 008 FR-027). */
export function PagesReset() {
  const t = useTranslations("shell.settings");
  const router = useRouter();
  return (
    <ConfirmAction
      label={t("resetLayouts")}
      question={t("resetLayoutsQuestion")}
      confirmLabel={t("resetLayoutsConfirm")}
      cancelLabel={t("resetLayoutsCancel")}
      onConfirm={async () => {
        await fetch("/api/v1/me/layouts", { method: "DELETE" });
        router.refresh();
      }}
    />
  );
}
