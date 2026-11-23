"use client";

import { useLocale, useTranslations } from "next-intl";
import type { ImpactReport } from "@/lib/experience/builder-types";
import { Button } from "@/components/ui/Button";
import { Dialog } from "@/components/ui/Dialog";
import { FactList } from "@/components/ui/Patterns";

function named(names: Record<string, string>, locale: string): string {
  return names[locale] ?? names.en ?? Object.values(names)[0] ?? "";
}

/**
 * The impact report before a shared artifact goes live (spec 032 R2 T024, FR-054): the organizations, audiences and
 * pages it reaches and the personal layouts it touches, with cancel. The report is computed by the server.
 */
export function ImpactDialog({
  report,
  submit,
  busy,
  onCancel,
  onConfirm,
}: {
  report: ImpactReport;
  /** Under an approval route the confirmation submits rather than publishes. */
  submit: boolean;
  busy: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const t = useTranslations("builder.impact");
  const locale = useLocale();
  const more = (shown: number, total: number) => (total > shown ? ` ${t("more", { count: String(total - shown) })}` : "");
  const units = report.tenantWide
    ? t("tenantWide")
    : report.orgUnits.map((u) => named(u.names, locale)).join(t("separator")) + more(report.orgUnits.length, report.orgUnitCount);
  const audiences = report.audiences.length === 0 ? t("everyone") : report.audiences.map((a) => named(a.labels, locale)).join(t("separator"));
  const pages = report.pages.map((p) => p.slug).join(t("separator")) + more(report.pages.length, report.pageCount);

  return (
    <Dialog
      open
      onClose={onCancel}
      title={t("title")}
      description={t("intro")}
      closeLabel={t("cancel")}
      footer={
        <>
          <Button variant="quiet" onClick={onCancel}>
            {t("cancel")}
          </Button>
          <Button variant="primary" busy={busy} busyLabel={t("working")} onClick={onConfirm}>
            {submit ? t("submit") : t("publish")}
          </Button>
        </>
      }
    >
      <div data-testid="builder-impact">
        <FactList
          facts={[
            { key: "units", label: t("units", { count: String(report.tenantWide ? 0 : report.orgUnitCount) }), value: <bdi>{units}</bdi> },
            { key: "audiences", label: t("audiences"), value: <bdi>{audiences}</bdi> },
            { key: "pages", label: t("pages", { count: String(report.pageCount) }), value: <bdi>{pages}</bdi> },
            {
              key: "layouts",
              label: t("layouts"),
              value: t("layoutsValue", { total: String(report.personalLayouts), touched: String(report.personalLayoutsTouched) }),
            },
          ]}
        />
        {report.personalLayoutsTouched > 0 ? (
          <p className="notice" role="note" data-testid="builder-impact-touched">
            {t("touched", { count: String(report.personalLayoutsTouched) })}
          </p>
        ) : null}
      </div>
    </Dialog>
  );
}
