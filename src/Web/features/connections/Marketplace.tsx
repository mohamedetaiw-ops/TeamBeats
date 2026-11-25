"use client";

import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";
import { Badge } from "@/components/ui/Badge";
import { ButtonLink } from "@/components/ui/Button";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { SearchField } from "@/components/ui/Inputs";
import { TYPE_GROUPS, isConnectionType, typeLook, wizardHref } from "@/lib/integrations/catalogue";
import type { Pack } from "@/lib/integrations/types";
import { ProductMark } from "./parts";

/**
 * The connector marketplace (spec 017 R3): every pack TeamBeats publishes, by what it connects, with its version, its
 * actions and their side effects, whether the package includes it, whether it is offered now (the platform can turn a
 * pack off for all tenants, with a reason), and how many connections the tenant has. Add opens the wizard. Every state
 * comes from the API.
 */
export function Marketplace({ packs }: { packs: Pack[] }) {
  const t = useTranslations("connections");
  const locale = useLocale();
  const [query, setQuery] = useState("");
  const name = (kind: string) => (isConnectionType(kind) ? t(`kinds.${kind}`) : t("kinds.other"));
  const description = (kind: string) => (kind === "custom_rest" ? t("catalogue.customRest") : isConnectionType(kind) ? t(`catalogue.${kind}`) : "");
  const needle = query.trim().toLocaleLowerCase();
  const shown = packs
    .filter((p) => !needle || name(p.kind).toLocaleLowerCase().includes(needle) || description(p.kind).toLocaleLowerCase().includes(needle))
    .sort((a, b) => name(a.kind).localeCompare(name(b.kind), locale));

  const columns: Column<Pack>[] = [
    {
      key: "type",
      header: t("marketplace.type"),
      rowHeader: true,
      sortValue: (p) => name(p.kind),
      cell: (p) => (
        <span className="connections-type-cell">
          <ProductMark kind={p.kind} size="sm" />
          <span>
            <strong>{name(p.kind)}</strong>
            <span className="field-hint">{description(p.kind)}</span>
          </span>
        </span>
      ),
    },
    {
      key: "state",
      header: t("marketplace.state"),
      nowrap: true,
      cell: (p) =>
        !p.available ? (
          <span>
            <Badge tone="warning">{t("marketplace.unavailable")}</Badge>
            {p.unavailableReason ? <span className="field-hint">{p.unavailableReason}</span> : null}
          </span>
        ) : p.inPackage ? (
          <Badge tone="success">{t("marketplace.inPackage")}</Badge>
        ) : (
          <Badge>{t("marketplace.notInPackage")}</Badge>
        ),
    },
    {
      key: "actions",
      header: t("marketplace.actions"),
      priority: "low",
      cell: (p) =>
        p.actions.length === 0
          ? t("marketplace.noActions")
          : t("marketplace.actionCount", {
              count: p.actions.length,
              reads: p.actions.filter((a) => a.sideEffect === "read").length,
              writes: p.actions.filter((a) => a.sideEffect !== "read").length,
            }),
    },
    { key: "version", header: t("marketplace.version"), align: "end", priority: "low", sortValue: (p) => p.version, cell: (p) => p.version },
    { key: "connections", header: t("marketplace.connections"), align: "end", sortValue: (p) => p.connections, cell: (p) => p.connections },
    {
      key: "add",
      header: t("marketplace.add"),
      nowrap: true,
      cell: (p) =>
        p.inPackage && p.available ? (
          <ButtonLink size="sm" href={wizardHref(p.kind)} aria-label={t("marketplace.addNamed", { name: name(p.kind) })}>
            {t("marketplace.add")}
          </ButtonLink>
        ) : null,
    },
  ];

  return (
    <section className="connections-section" aria-labelledby="marketplace-heading">
      <div className="connections-section-head">
        <h2 id="marketplace-heading">{t("marketplace.title")}</h2>
        <p>{t("marketplace.intro")}</p>
      </div>
      <SearchField label={t("marketplace.search")} value={query} onValueChange={setQuery} />
      {TYPE_GROUPS.map((group) => {
        const rows = shown.filter((p) => typeLook(p.kind).group === group);
        if (rows.length === 0) return null;
        return (
          <DataTable
            key={group}
            caption={t(`wizard.groups.${group}`)}
            columns={columns}
            rows={rows}
            rowKey={(p) => p.kind}
            rowTestId={(p) => `pack-${p.kind}`}
            testId={`marketplace-${group}`}
          />
        );
      })}
      {shown.length === 0 ? <p className="field-hint">{t("wizard.choose.noMatch")}</p> : null}
    </section>
  );
}
