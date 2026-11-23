"use client";

import Link from "next/link";
import { useId, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable, type Column } from "@/components/ui/DataTable";
import type { ComponentDeployments, ComponentUpgradeOption, ComponentUpgradeResult } from "@/lib/experience/builder-types";
import { useHydrated } from "@/lib/clock";
import { SendMessages, useSend } from "./useSend";

/**
 * Component upgrades (spec 032 R2 T031, FR-033): deploy a component's next major to the organization's pages. The migration
 * hook converts drafts, and live pages show the new major at once without a republish; a rollback brings the old one back.
 * Which upgrades exist and what each one changes is the API's answer.
 */
export function ComponentUpgrades({ initial }: { initial: ComponentDeployments }) {
  const t = useTranslations("builder.manage.upgrades");
  const format = useFormatter();
  const hydrated = useHydrated();
  const outcomeId = useId();
  const [view, setView] = useState(initial);
  const [outcome, setOutcome] = useState<{ option: ComponentUpgradeOption; result: ComponentUpgradeResult } | null>(null);
  const { busy, result, send } = useSend(t("failed"));

  async function reload() {
    const response = await fetch("/api/v1/experience/components/deployments", { cache: "no-store" });
    if (response.ok) setView((await response.json()) as ComponentDeployments);
  }

  const deployedAt = (o: ComponentUpgradeOption) => view.deployed.find((d) => d.from === o.from)?.deployedAt;

  const columns: Column<ComponentUpgradeOption>[] = [
    { key: "name", header: t("component"), rowHeader: true, cell: (o) => <bdi>{o.name}</bdi> },
    {
      key: "change",
      header: t("change"),
      cell: (o) => (
        <bdi>{t("changeLine", { from: o.from, to: o.to })}</bdi>
      ),
    },
    {
      key: "state",
      header: t("state"),
      cell: (o) => {
        const at = deployedAt(o);
        return (
          <Badge tone={o.deployed ? "success" : "neutral"}>
            {o.deployed ? (at && hydrated ? t("deployedOn", { when: format.dateTime(new Date(at), { dateStyle: "medium" }) }) : t("deployed")) : t("notDeployed")}
          </Badge>
        );
      },
    },
    {
      key: "manage",
      header: t("manage"),
      align: "end",
      cell: (o) =>
        o.deployed ? (
          <ConfirmAction
            label={t("rollBack")}
            question={t("rollBackQuestion", { name: o.name, from: o.from })}
            confirmLabel={t("rollBackConfirm")}
            cancelLabel={t("cancel")}
            onConfirm={async () => {
              if (await send(`/api/v1/experience/components/${encodeURIComponent(o.componentId)}/deployments/${o.fromMajor}`, "DELETE", null, t("rolledBack"))) {
                setOutcome(null);
                await reload();
              }
            }}
          />
        ) : (
          <Button
            size="sm"
            busy={busy}
            busyLabel={t("working")}
            aria-label={t("deployLabel", { name: o.name })}
            onClick={async () => {
              const done = await send<ComponentUpgradeResult>(
                `/api/v1/experience/components/${encodeURIComponent(o.componentId)}/upgrade`,
                "POST",
                { fromMajor: o.fromMajor },
                t("deployedDone"),
              );
              if (done) {
                setOutcome({ option: o, result: done });
                await reload();
              }
            }}
          >
            {t("deploy")}
          </Button>
        ),
    },
  ];

  return (
    <div className="builder-manage" data-testid="component-upgrades">
      <DataTable
        caption={t("caption")}
        columns={columns}
        rows={view.available}
        rowKey={(o) => o.from}
        emptyTitle={t("emptyTitle")}
        emptyMessage={t("empty")}
        density="compact"
      />
      {outcome ? (
        <section className="builder-manage-form" aria-labelledby={outcomeId} data-testid="component-upgrade-outcome">
          <h2 id={outcomeId}>{t("outcomeTitle", { name: outcome.option.name })}</h2>
          <p>{t("outcomeDrafts", { count: outcome.result.upgraded.length })}</p>
          {outcome.result.blocked.length > 0 ? (
            <>
              <p className="notice" role="note">
                {t("outcomeBlocked", { count: outcome.result.blocked.length })}
              </p>
              <ul className="builder-manage-list">
                {outcome.result.blocked.map((b) => (
                  <li key={`${b.pageId}-${b.placementId}`}>
                    <Link href={`/admin/pages/${b.pageId}`}>{t("blockedLine", { placement: b.placementId })}</Link>
                  </li>
                ))}
              </ul>
            </>
          ) : null}
          <p className="field-hint">{t("outcomeLive")}</p>
        </section>
      ) : null}
      <SendMessages result={result} />
    </div>
  );
}
