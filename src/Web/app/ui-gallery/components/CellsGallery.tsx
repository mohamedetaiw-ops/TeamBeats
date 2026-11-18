"use client";

import { useTranslations } from "next-intl";
import { DataTable } from "@/components/ui/DataTable";
import { orDash } from "@/components/ui/EmptyCell";
import { KeyName } from "@/components/ui/KeyName";

/** Stored keys and empty cells (spec 036 DLS-093, DLS-095): a key shows by its name, an empty cell as a dash. */
const GALLERY_KEYS: { code: string; name: string; scope: string | null }[] = [
  { code: "audit.read", name: "auditRead", scope: "scopeTenant" },
  { code: "content.publish", name: "contentPublish", scope: null },
];

/**
 * Table cells of settings pages (spec 036 DLS-093, DLS-095). Last on the page, so its height does not shift the
 * sections above it that the design suite screenshots.
 */
export function CellsGallery() {
  const t = useTranslations("components_gallery");
  return (
    <section aria-labelledby="cg-cells">
      <h2 id="cg-cells">{t("cells.title")}</h2>
      <p className="notice">{t("cells.intro")}</p>
      <DataTable
        caption={t("cells.title")}
        captionHidden
        rows={GALLERY_KEYS}
        rowKey={(r) => r.code}
        columns={[
          { key: "name", header: t("cells.colPermission"), rowHeader: true, cell: (r) => <KeyName name={t(`cells.${r.name}`)} code={r.code} /> },
          { key: "scope", header: t("cells.colScope"), cell: (r) => orDash(r.scope ? t(`cells.${r.scope}`) : null) },
        ]}
      />
    </section>
  );
}
