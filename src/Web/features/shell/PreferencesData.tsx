"use client";

import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { ButtonLink } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";

/**
 * The person's own preferences as a file, and a reset (spec 008 FR-034): the export is the documented
 * `teambeats.preferences` JSON with only their own choices; reset returns appearance on every device and every page
 * they arranged to the organization's defaults, after a second step. Favorites and recent visits stay.
 */
export function PreferencesData() {
  const t = useTranslations("preferences.data");
  const router = useRouter();
  return (
    <div className="preferences-actions">
      <ButtonLink href="/api/v1/me/preferences/export" icon="download" download="teambeats-preferences.json">
        {t("export")}
      </ButtonLink>
      <ConfirmAction
        label={t("reset")}
        question={t("resetQuestion")}
        confirmLabel={t("resetConfirm")}
        cancelLabel={t("cancel")}
        onConfirm={async () => {
          await fetch("/api/v1/me/preferences", { method: "DELETE" });
          router.refresh();
        }}
      />
    </div>
  );
}
