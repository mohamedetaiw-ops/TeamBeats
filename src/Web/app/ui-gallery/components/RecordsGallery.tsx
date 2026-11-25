"use client";

import { useTranslations } from "next-intl";
import { FormField, FormPanel, SettingsSection } from "@/components/ui/FormField";
import { WorkItems } from "@/components/ui/WorkItems";

const changed = "2026-11-24T09:30:00Z";

/**
 * Records and settings parts (spec 036 DLS-047, DLS-061): work item rows that each open a page, one with a long title
 * and no meta, a settings section with its help beside the controls, and a form panel around a group of fields.
 */
export function RecordsGallery() {
  const t = useTranslations("components_gallery.records");
  return (
    <section aria-labelledby="cg-records">
      <h2 id="cg-records">{t("title")}</h2>
      <p className="notice">{t("intro")}</p>
      <WorkItems
        testId="cg-work-items"
        items={[
          { id: "r1", href: "#cg-records", title: t("laptop"), number: "REQ-1042", rest: t("laptopMeta"), status: { text: t("inProgress"), tone: "info" }, changed: { dateTime: changed, full: t("changedFull"), text: t("changed") } },
          { id: "r2", href: "#cg-records", title: t("longTitle"), number: "REQ-1043", status: { text: t("done"), tone: "success" }, changed: { dateTime: changed, full: t("changedFull"), text: t("changed") } },
        ]}
      />
      <SettingsSection id="cg-settings-section" icon="bell" title={t("settingsTitle")} hint={t("settingsHint")}>
        <FormField label={t("digest")} name="cg-digest" />
      </SettingsSection>
      <FormPanel as="fieldset">
        <legend>{t("panelTitle")}</legend>
        <FormField label={t("reason")} name="cg-reason" />
      </FormPanel>
    </section>
  );
}
