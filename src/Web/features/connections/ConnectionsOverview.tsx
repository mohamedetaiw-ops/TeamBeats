"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useMemo, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button, ButtonLink } from "@/components/ui/Button";
import { SearchField } from "@/components/ui/Inputs";
import { StateView } from "@/components/ui/StateView";
import { NEW_CONNECTION, isConnectionType, manageHref, type OverviewRow } from "@/lib/integrations/catalogue";
import type { Microsoft365TestResult, PackTestResult } from "@/lib/integrations/types";
import { Messages, ProductMark, StatusBadge } from "./parts";
import { send } from "./request";

const RETURN_TO = "/admin/connections";

/** Connections that need someone first, then working ones, then those turned off (spec 006 FR-010). */
const ORDER: Record<string, number> = { degraded: 0, needs_consent: 1, credential_expiring: 2, healthy: 3, disabled: 4 };
const needsAttention = (status: string) => (ORDER[status] ?? 0) < 3;

function base(row: OverviewRow) {
  return row.kind === "microsoft365" ? `/api/v1/integrations/connections/${row.id}` : `/api/v1/integrations/pack-connections/${row.id}`;
}

function Row({ row }: { row: OverviewRow }) {
  const t = useTranslations("connections");
  const format = useFormatter();
  const router = useRouter();
  const [busy, setBusy] = useState<"test" | "toggle" | null>(null);
  const [result, setResult] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const typeName = isConnectionType(row.kind) ? t(`kinds.${row.kind}`) : t("kinds.other");

  async function test() {
    setBusy("test");
    setResult({ ok: true, list: [] });
    const outcome = await send<PackTestResult | Microsoft365TestResult>("POST", `${base(row)}:test`, undefined, RETURN_TO, t("detail.failed"));
    setBusy(null);
    if (!outcome.ok) {
      setResult({ ok: false, list: outcome.messages });
      return;
    }
    const passed = "features" in outcome.data ? outcome.data.features.every((f) => f.ok) : outcome.data.status === "ok";
    setResult({ ok: passed, list: [t(passed ? "detail.testPassed" : "overview.testFailed")] });
    router.refresh();
  }

  async function toggle() {
    setBusy("toggle");
    setResult({ ok: true, list: [] });
    const outcome = await send<unknown>("POST", `${base(row)}:${row.enabled ? "disable" : "enable"}`, undefined, RETURN_TO, t("detail.failed"));
    setBusy(null);
    if (outcome.ok) {
      setResult({ ok: true, list: [t(row.enabled ? "detail.disabled" : "detail.enabled")] });
      router.refresh();
    } else {
      setResult({ ok: false, list: outcome.messages });
    }
  }

  return (
    <li className="connections-entry" data-testid="connection-row" data-status={row.status}>
      <ProductMark kind={row.kind} />
      <div className="connections-entry-text">
        <h3 className="connections-entry-name">
          <Link href={manageHref(row.kind, row.id)}>
            <bdi>{row.name}</bdi>
          </Link>
        </h3>
        <p className="connections-entry-meta">
          <span>{typeName}</span>
          <span>{row.kind === "microsoft365" ? t("m365.featuresOn", { count: row.count }) : t("list.actions", { count: row.count })}</span>
          <span>
            {row.lastSuccessAt
              ? t("list.lastSuccess", { at: format.dateTime(new Date(row.lastSuccessAt), { dateStyle: "medium", timeStyle: "short" }) })
              : t("overview.neverRan")}
          </span>
        </p>
      </div>
      <div className="connections-entry-status">
        <StatusBadge status={row.status} />
      </div>
      <div className="connections-entry-actions">
        <Button size="sm" busy={busy === "test"} busyLabel={t("detail.working")} aria-label={t("overview.testOf", { name: row.name })} onClick={() => void test()}>
          {t("overview.test")}
        </Button>
        <Button
          size="sm"
          variant="quiet"
          busy={busy === "toggle"}
          busyLabel={t("detail.working")}
          aria-label={t(row.enabled ? "overview.turnOffOf" : "overview.turnOnOf", { name: row.name })}
          onClick={() => void toggle()}
        >
          {t(row.enabled ? "detail.disable" : "detail.enable")}
        </Button>
        <ButtonLink size="sm" variant="quiet" href={manageHref(row.kind, row.id)} aria-label={t("overview.manageOf", { name: row.name })}>
          {t("overview.manage")}
        </ButtonLink>
      </div>
      {result.list.length > 0 ? (
        <div className="connections-entry-result">
          <Messages ok={result.ok} messages={result.list} />
        </div>
      ) : null}
    </li>
  );
}

/**
 * The established connections of the tenant, for managing them (spec 006 US5, FR-010): each with its product mark,
 * type, status, what it does and its last success, and quick Test, Turn on or off and Manage actions. Adding a
 * connection is a separate wizard (FR-016). The API decides every rule; this list only shows its answers.
 */
export function ConnectionsOverview({ rows }: { rows: OverviewRow[] }) {
  const t = useTranslations("connections");
  const [query, setQuery] = useState("");
  const sorted = useMemo(() => [...rows].sort((a, b) => (ORDER[a.status] ?? 0) - (ORDER[b.status] ?? 0) || a.name.localeCompare(b.name)), [rows]);
  const needle = query.trim().toLocaleLowerCase();
  const typeName = (kind: string) => (isConnectionType(kind) ? t(`kinds.${kind}`) : t("kinds.other"));
  const shown = needle ? sorted.filter((r) => r.name.toLocaleLowerCase().includes(needle) || typeName(r.kind).toLocaleLowerCase().includes(needle)) : sorted;
  const attention = rows.filter((r) => needsAttention(r.status)).length;
  const off = rows.filter((r) => !r.enabled).length;

  if (rows.length === 0) {
    return (
      <section className="connections-section" aria-labelledby="connections-heading">
        <h2 id="connections-heading">{t("list.title")}</h2>
        <StateView
          kind="empty"
          icon="link"
          title={t("list.empty")}
          message={t("overview.emptyMessage")}
          action={
            <ButtonLink variant="primary" icon="plus" href={NEW_CONNECTION}>
              {t("overview.add")}
            </ButtonLink>
          }
        />
      </section>
    );
  }

  return (
    <section className="connections-section" aria-labelledby="connections-heading">
      <div className="connections-section-head">
        <h2 id="connections-heading">{t("list.title")}</h2>
        <p className="connections-summary" data-testid="connections-summary">
          {t("overview.summary", { total: rows.length, attention, off })}
        </p>
      </div>
      {rows.length > 5 ? <SearchField label={t("overview.search")} value={query} onValueChange={setQuery} /> : null}
      {shown.length === 0 ? (
        <StateView kind="empty" icon="search" title={t("overview.noMatch")} message="" />
      ) : (
        <ul className="connections-entries">
          {shown.map((row) => (
            <Row key={`${row.kind}:${row.id}`} row={row} />
          ))}
        </ul>
      )}
    </section>
  );
}
