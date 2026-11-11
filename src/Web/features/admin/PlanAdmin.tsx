"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { StatCards } from "@/components/ui/Blocks";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { byArea, type Entitlements } from "@/lib/admin/types";
import { useKeyLabel } from "./shared";

const FILTERS = ["all", "included", "missing"] as const;

/**
 * Plan (spec 005 US3): the package this workspace subscribes to and every capability it includes, with limits. It is
 * read-only: the package is changed by TeamBeats, not in the workspace. Features check capability keys through the
 * entitlement service, never this screen.
 */
export function PlanAdmin({ plan }: { plan: Entitlements }) {
  const t = useTranslations("admin.plan");
  const format = useFormatter();
  const area = useKeyLabel("admin.areas");
  const capability = useKeyLabel("admin.plan.capabilities");
  const [filter, setFilter] = useState<(typeof FILTERS)[number]>("all");
  const entries = Object.entries(plan.capabilities).map(([key, value]) => ({ key, ...value }));
  const included = entries.filter((e) => e.enabled);
  const limits = included.filter((e) => e.limit !== null);
  const rows = entries.filter((e) => (filter === "all" ? true : filter === "included" ? e.enabled : !e.enabled));

  return (
    <div className="admin-screen">
      <StatCards
        label={t("summary")}
        stats={[
          { key: "package", label: t("package"), value: t.has(`packages.${plan.packageKey}`) ? t(`packages.${plan.packageKey}`) : plan.packageKey },
          { key: "version", label: t("version"), value: format.number(plan.version) },
          { key: "since", label: t("since"), value: format.dateTime(new Date(plan.startsAt), { dateStyle: "medium" }) },
          { key: "included", label: t("included"), value: t("includedValue", { count: included.length, total: entries.length }) },
        ]}
      />
      <p className="admin-meta">{t("changeHint")}</p>
      <SegmentedControl
        label={t("filter")}
        name="plan-filter"
        value={filter}
        options={FILTERS.map((f) => ({ value: f, label: t(`filters.${f}`) }))}
        onChange={(f) => setFilter(f as (typeof FILTERS)[number])}
      />
      {byArea(rows, (r) => r.key).map(([a, list]) => (
        <section key={a} className="admin-panel" aria-labelledby={`plan-${a}`}>
          <h2 id={`plan-${a}`}>{area(a)}</h2>
          <ul className="admin-capabilities">
            {list.map((c) => (
              <li key={c.key} data-enabled={c.enabled || undefined}>
                <span className="admin-capability-text">
                  <span>{capability(c.key.replaceAll(".", "_"))}</span>
                  <code dir="ltr">{c.key}</code>
                </span>
                <span className="admin-badges">
                  {c.enabled && c.limit !== null ? <Badge tone="info">{t("limit", { limit: format.number(c.limit) })}</Badge> : null}
                  <Badge tone={c.enabled ? "success" : "neutral"} icon={c.enabled ? "check" : "minus"}>
                    {t(c.enabled ? "yes" : "no")}
                  </Badge>
                </span>
              </li>
            ))}
          </ul>
        </section>
      ))}
      {limits.length === 0 ? null : <p className="admin-meta">{t("limitsHint")}</p>}
    </div>
  );
}
