"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import type { AckReportView } from "@/lib/content/types";

/** Requests the completion report, which is built in the background (spec 009 US2), and offers the file when ready. */
export function AckReport({ itemId }: { itemId: string }) {
  const t = useTranslations("content.acks");
  const [report, setReport] = useState<AckReportView | null>(null);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);

  useEffect(() => {
    if (!report || report.status === "completed" || report.status === "failed") return;
    const timer = setTimeout(() => {
      fetch(`/api/v1/content/ack-reports/${encodeURIComponent(report.id)}`, { cache: "no-store" })
        .then(async (r) => r.ok && setReport((await r.json()) as AckReportView))
        .catch(() => undefined);
    }, 2000);
    return () => clearTimeout(timer);
  }, [report]);

  async function request() {
    setBusy(true);
    setErrors([]);
    const response = await fetch(`/api/v1/content/items/${encodeURIComponent(itemId)}/ack-report`, { method: "POST" }).catch(() => null);
    setBusy(false);
    if (response?.ok) {
      setReport((await response.json()) as AckReportView);
      return;
    }
    setErrors(response ? await problemMessages(response) : [t("failed")]);
  }

  return (
    <div className="content-card-body">
      <div className="content-actions">
        <Button busy={busy} busyLabel={t("requesting")} onClick={() => void request()}>
          {t("requestReport")}
        </Button>
      </div>
      <p className="content-meta" aria-live="polite">
        {report ? t(`reportStatus.${report.status === "completed" || report.status === "failed" ? report.status : "pending"}`) : null}
      </p>
      {report?.status === "completed" ? (
        <a href={`/api/v1/content/ack-reports/${encodeURIComponent(report.id)}/file`} download>
          {t("download", { rows: report.rowCount ?? 0 })}
        </a>
      ) : null}
      {errors.length > 0 ? (
        <p className="field-error" role="alert">
          {errors.join(" ")}
        </p>
      ) : null}
    </div>
  );
}
