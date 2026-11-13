"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable } from "@/components/ui/DataTable";
import { StateView } from "@/components/ui/StateView";
import { ResultAlert, useAdminAction, useWhen } from "@/features/admin/shared";
import type { AccessReviewItem } from "@/lib/security/types";

const RETURN_TO = "/access-reviews";

/**
 * The reviewer's list (spec 026 FR-012): the guests they invited and the administrator roles of the people they manage.
 * Confirm keeps the access; remove ends it at once. The API checks that each item is theirs to decide.
 */
export function AccessReviewList({ initial }: { initial: AccessReviewItem[] }) {
  const t = useTranslations("accessReviews");
  const when = useWhen();
  const action = useAdminAction(RETURN_TO);
  const [items, setItems] = useState(initial);

  async function decide(item: AccessReviewItem, verb: "confirm" | "remove") {
    const outcome = await action.run<{ items: AccessReviewItem[] }>(verb + item.id, "POST", `/api/v1/access-reviews/items/${item.id}:${verb}`, undefined,
      verb === "confirm" ? t("confirmed", { name: item.subjectName }) : t("removed", { name: item.subjectName }));
    if (outcome.ok) setItems(outcome.data.items);
  }

  if (items.length === 0) {
    return <StateView kind="empty" title={t("noneTitle")} message={t("none")} icon="check" />;
  }
  return (
    <div className="security">
      <p className="notice">{t("due", { date: when(items[0]!.dueAt) })}</p>
      <ResultAlert result={action.result} />
      <DataTable
        caption={t("listTitle")}
        rows={items}
        rowKey={(i) => i.id}
        columns={[
          { key: "who", header: t("who"), rowHeader: true, cell: (i) => <bdi>{i.subjectName}</bdi> },
          {
            key: "access",
            header: t("access"),
            cell: (i) =>
              i.kind === "guest" ? (
                <>
                  {t("guestIn")} <bdi>{i.spaceName}</bdi>
                </>
              ) : (
                <>
                  {t("role")} <code dir="ltr">{i.roleKey}</code>
                </>
              ),
          },
          {
            key: "decision",
            header: t("decision"),
            cell: (i) => (i.decision ? <Badge tone={i.decision === "confirmed" ? "success" : "neutral"}>{t(`decisions.${i.decision}`)}</Badge> : t("waiting")),
          },
          {
            key: "actions",
            header: t("actions"),
            align: "end",
            cell: (i) =>
              i.decision ? null : (
                <div className="admin-row">
                  <Button size="sm" variant="secondary" busy={action.busy === "confirm" + i.id} busyLabel={t("working")} onClick={() => void decide(i, "confirm")}>
                    {t("confirm")}
                  </Button>
                  <ConfirmAction
                    label={t("remove")}
                    question={t("removeQuestion", { name: i.subjectName })}
                    confirmLabel={t("remove")}
                    cancelLabel={t("cancel")}
                    onConfirm={() => decide(i, "remove")}
                  />
                </div>
              ),
          },
        ]}
      />
    </div>
  );
}
