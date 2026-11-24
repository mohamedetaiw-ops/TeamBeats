"use client";

import { useMemo, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { StatCards } from "@/components/ui/Blocks";
import { Button } from "@/components/ui/Button";
import { SearchField } from "@/components/ui/Inputs";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { StateView } from "@/components/ui/StateView";
import { Meter } from "@/components/ui/Status";
import { areaOf, type Entitlements, type PlanGrant, type QuotaUsage } from "@/lib/admin/types";
import { useKeyLabel } from "./shared";

const FILTERS = ["all", "included", "missing"] as const;
type Filter = (typeof FILTERS)[number];

type Row = { key: string; name: string; enabled: boolean; limit: number | null; source: string };

/** Share of a quota at which its bar turns to a warning (spec 005 section 12 notifies at exhaustion; the bar warns earlier). */
const WARN_SHARE = 0.8;

/**
 * Plan (spec 005 US3, DLS-068): what the package includes, grouped by area in two columns, searchable by name.
 * Capabilities show their localized names only; the technical keys stay out of the screen. R2 (T014 extended) adds the
 * contract end and its grace or read-only state, this month's use of each metered quota, and the add-ons and contract
 * terms with their end dates. Every value comes from the API; the page decides nothing.
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
    source: plan.sources?.[key] ?? "package",
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
  const date = (value: string) => format.dateTime(new Date(value), { dateStyle: "medium" });
  const contract = plan.contract ?? null;
  const usage = plan.usage ?? [];
  const grants: (PlanGrant & { kind: "addon" | "override" })[] = [
    ...(plan.addons ?? []).map((g) => ({ ...g, kind: "addon" as const })),
    ...(plan.overrides ?? []).map((g) => ({ ...g, kind: "override" as const })),
  ].sort((x, y) => collator.compare(capability(x.capabilityKey.replaceAll(".", "_")), capability(y.capabilityKey.replaceAll(".", "_"))));

  return (
    <div className="admin-screen">
      {contract?.state === "grace" && contract.contractEndsAt && contract.graceEndsAt ? (
        <Alert tone="warning" title={t("contract.graceTitle")}>
          {t("contract.graceBody", { ended: date(contract.contractEndsAt), readOnly: date(contract.graceEndsAt) })}
        </Alert>
      ) : null}
      {contract?.state === "read_only" && contract.contractEndsAt ? (
        <Alert tone="danger" title={t("contract.readOnlyTitle")}>
          {t("contract.readOnlyBody", { ended: date(contract.contractEndsAt) })}
        </Alert>
      ) : null}
      <StatCards
        label={t("summary")}
        stats={[
          { key: "package", label: t("package"), value: packageName, icon: "star" },
          { key: "since", label: t("since"), value: date(plan.startsAt), icon: "calendar" },
          ...(contract?.contractEndsAt ? [{ key: "ends", label: t("contract.ends"), value: date(contract.contractEndsAt), icon: "clock" as const }] : []),
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
      {usage.length > 0 ? <UsagePanel usage={usage} name={(key) => capability(key.replaceAll(".", "_"))} /> : null}
      {grants.length > 0 ? (
        <section className="admin-panel" aria-labelledby="plan-grants">
          <div className="admin-panel-head">
            <h2 id="plan-grants">{t("grants.title")}</h2>
          </div>
          <ul className="admin-capabilities">
            {grants.map((g) => (
              <li key={`${g.kind}-${g.capabilityKey}`} data-enabled={(g.active && g.enabled) || undefined}>
                <span className="admin-capability-text">
                  <span>{capability(g.capabilityKey.replaceAll(".", "_"))}</span>
                  <span className="admin-meta">
                    {g.validTo ? t("grants.window", { from: date(g.validFrom), to: date(g.validTo) }) : t("grants.from", { from: date(g.validFrom) })}
                  </span>
                </span>
                <span className="admin-badges">
                  <Badge tone="accent">{t(`grants.kinds.${g.kind}`)}</Badge>
                  {g.enabled && g.limit !== null ? <Badge tone="info">{t("limit", { limit: format.number(g.limit) })}</Badge> : null}
                  <Badge tone={g.active ? "success" : "neutral"} icon={g.active ? "check" : "minus"}>
                    {g.active ? t("grants.active") : t("grants.inactive")}
                  </Badge>
                </span>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
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
                        {c.source !== "package" && t.has(`grants.kinds.${c.source}`) ? <Badge tone="accent">{t(`grants.kinds.${c.source}`)}</Badge> : null}
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

/** This month's use of each metered quota; the bar warns from 80 percent and turns red at the limit. */
function UsagePanel({ usage, name }: { usage: QuotaUsage[]; name: (key: string) => string }) {
  const t = useTranslations("admin.plan");
  const format = useFormatter();
  return (
    <section className="admin-panel" aria-labelledby="plan-usage">
      <div className="admin-panel-head">
        <h2 id="plan-usage">{t("usage.title")}</h2>
        <span className="admin-meta">{t("usage.period")}</span>
      </div>
      <div className="admin-plan-usage">
        {usage.map((u) => {
          const share = u.limit && u.limit > 0 ? u.used / u.limit : 0;
          const tone = u.limit === null ? "accent" : share >= 1 ? "danger" : share >= WARN_SHARE ? "warning" : "success";
          return (
            <Meter
              key={u.capabilityKey}
              label={name(u.capabilityKey)}
              value={u.used}
              max={u.limit ?? Math.max(u.used, 1)}
              tone={tone}
              valueText={
                u.limit === null
                  ? t("usage.usedOnly", { used: format.number(u.used) })
                  : t("usage.usedOf", { used: format.number(u.used), limit: format.number(u.limit) })
              }
            />
          );
        })}
      </div>
      <p className="admin-meta">{t("usage.hint")}</p>
    </section>
  );
}
