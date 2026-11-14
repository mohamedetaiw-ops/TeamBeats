"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { ResultAlert, useAdminAction, useWhen } from "@/features/admin/shared";
import type { AccessReviewsPage, AccessReviewSummary } from "@/lib/security/types";

const RETURN_TO = "/admin/security/reviews";

/**
 * Access reviews for security administrators (spec 026 FR-012): every review with what was confirmed, removed or left
 * to lapse, the date of the next one, and a way to open one now. Opening needs a fresh sign-in; the API checks it.
 */
export function AccessReviewAdmin({ initial }: { initial: AccessReviewsPage }) {
  const t = useTranslations("security.reviews");
  const when = useWhen();
  const action = useAdminAction(RETURN_TO);
  const [page, setPage] = useState(initial);

  async function reload() {
    const response = await fetch("/api/v1/security/access-reviews", { cache: "no-store" }).catch(() => null);
    if (response?.ok) setPage((await response.json()) as AccessReviewsPage);
  }

  return (
    <div className="security">
      <p className="notice">{page.nextAt ? t("next", { date: when(page.nextAt) }) : t("noneScheduled")}</p>
      <ResultAlert result={action.result} />
      {page.canStart ? (
        <div className="ui-control-row">
          <Button
            variant="primary"
            busy={action.busy === "start"}
            busyLabel={t("working")}
            onClick={async () => {
              const outcome = await action.run("start", "POST", "/api/v1/security/access-reviews", undefined, t("started"));
              if (outcome.ok) await reload();
            }}
          >
            {t("start")}
          </Button>
        </div>
      ) : null}
      <DataTable<AccessReviewSummary>
        caption={t("listTitle")}
        rows={page.reviews}
        rowKey={(r) => r.id}
        emptyTitle={t("none")}
        emptyMessage={t("noneHint")}
        columns={[
          { key: "opened", header: t("opened"), rowHeader: true, cell: (r) => when(r.openedAt) },
          { key: "due", header: t("dueAt"), cell: (r) => (r.closedAt ? t("closed", { date: when(r.closedAt) }) : when(r.dueAt)) },
          { key: "open", header: t("open"), align: "end", cell: (r) => r.open },
          { key: "confirmed", header: t("confirmed"), align: "end", cell: (r) => r.confirmed },
          { key: "removed", header: t("removed"), align: "end", cell: (r) => r.removed + r.expired },
          { key: "kept", header: t("kept"), align: "end", cell: (r) => r.kept },
        ]}
      />
    </div>
  );
}
