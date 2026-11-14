"use client";

import { useMemo, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { StatCards } from "@/components/ui/Blocks";
import { Button } from "@/components/ui/Button";
import { SearchField } from "@/components/ui/Inputs";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { StateView } from "@/components/ui/StateView";
import { areaOf, type Entitlements } from "@/lib/admin/types";
import { useKeyLabel } from "./shared";

const FILTERS = ["all", "included", "missing"] as const;
type Filter = (typeof FILTERS)[number];

type Row = { key: string; name: string; enabled: boolean; limit: number | null };

/**
 * Plan (spec 005 US3, DLS-068): what the package includes, grouped by area in two columns, searchable by name.
 * Capabilities show their localized names only; the technical keys stay out of the screen.
 */
export function PlanAdmin({ plan }: { plan: Entitlements }) {
  const t = useTranslations("admin.plan");
  const format = useFormatter();
  const locale = useLocale();
  const area = useKeyLabel("admin.areas");
  const capability = useKeyLabel("admin.plan.capabilities");
  const [filter, setFilter] = useState<Filter>("all");
  const [query, setQuery] = useState("");

  const collator = useMemo(() => new Intl.Collator(locale, { sensitivity: "base" }), [locale]);
  const entries: Row[] = Object.entries(plan.capabilities).map(([key, value]) => ({
    key,
    name: capability(key.replaceAll(".", "_")),
    enabled: value.enabled,
    limit: value.limit,
  }));
  const included = entries.filter((e) => e.enabled);
  const needle = query.trim().toLocaleLowerCase(locale);
  const matching = needle === "" ? entries : entries.filter((e) => e.name.toLocaleLowerCase(locale).includes(needle));
  const inFilter = (e: Row, f: Filter) => (f === "all" ? true : f === "included" ? e.enabled : !e.enabled);
  const rows = matching.filter((e) => inFilter(e, filter));

  const groups = new Map<string, Row[]>();
  for (const row of rows) {
    const a = areaOf(row.key);
    groups.set(a, [...(groups.get(a) ?? []), row]);
  }
  const totals = new Map<string, { count: number; total: number }>();
  for (const e of entries) {
    const a = areaOf(e.key);
    const current = totals.get(a) ?? { count: 0, total: 0 };
    totals.set(a, { count: current.count + (e.enabled ? 1 : 0), total: current.total + 1 });
  }
  const sorted = [...groups.entries()]
    .map(([a, list]) => ({ key: a, name: area(a), list: [...list].sort((x, y) => collator.compare(x.name, y.name)) }))
    .sort((x, y) => collator.compare(x.name, y.name));
  const packageName = t.has(`packages.${plan.packageKey}`) ? t(`packages.${plan.packageKey}`) : plan.packageKey;

  return (
    <div className="admin-screen">
      <StatCards
        label={t("summary")}
        stats={[
          { key: "package", label: t("package"), value: packageName, icon: "star" },
          { key: "since", label: t("since"), value: format.dateTime(new Date(plan.startsAt), { dateStyle: "medium" }), icon: "calendar" },
          {
            key: "included",
            label: t("included"),
            value: t("includedValue", { count: included.length, total: entries.length }),
            icon: "check",
          },
        ]}
      />
      <p className="admin-meta">
        <strong>{t("versionMeta", { version: format.number(plan.version) })}</strong>
        <br />
        {t("changeHint")}
      </p>
      <div className="admin-toolbar" role="search">
        <SearchField label={t("searchLabel")} name="plan-search" placeholder={t("searchPlaceholder")} value={query} onValueChange={setQuery} />
        <SegmentedControl
          label={t("filter")}
          name="plan-filter"
          value={filter}
          options={FILTERS.map((f) => ({
            value: f,
            label: t("filterCount", { label: t(`filters.${f}`), count: matching.filter((e) => inFilter(e, f)).length }),
          }))}
          onChange={(f) => setFilter(f as Filter)}
        />
      </div>
      {sorted.length === 0 ? (
        <StateView
          kind="empty"
          icon="search"
          title={t("emptyTitle")}
          message={t("emptyBody")}
          action={
            <Button
              variant="secondary"
              onClick={() => {
                setQuery("");
                setFilter("all");
              }}
            >
              {t("clear")}
            </Button>
          }
        />
      ) : (
        <div className="admin-plan-areas">
          {sorted.map((g) => {
            const total = totals.get(g.key) ?? { count: 0, total: 0 };
            return (
              <section key={g.key} className="admin-panel" aria-labelledby={`plan-${g.key}`}>
                <div className="admin-panel-head">
                  <h2 id={`plan-${g.key}`}>{g.name}</h2>
                  <span className="admin-meta">{t("areaCount", { count: total.count, total: total.total })}</span>
                </div>
                <ul className="admin-capabilities">
                  {g.list.map((c) => (
                    <li key={c.key} data-enabled={c.enabled || undefined}>
                      <span className="admin-capability-text">
                        <span>{c.name}</span>
                      </span>
                      <span className="admin-badges">
                        {c.enabled && c.limit !== null ? <Badge tone="info">{t("limit", { limit: format.number(c.limit) })}</Badge> : null}
                        <Badge tone={c.enabled ? "success" : "neutral"} icon={c.enabled ? "check" : "minus"}>
                          {c.enabled ? t("yes") : t("no")}
                        </Badge>
                      </span>
                    </li>
                  ))}
                </ul>
              </section>
            );
          })}
        </div>
      )}
      {included.some((e) => e.limit !== null) ? <p className="admin-meta">{t("limitsHint")}</p> : null}
    </div>
  );
}
