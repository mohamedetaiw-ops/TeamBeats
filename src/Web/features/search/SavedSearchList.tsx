"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { Skeleton } from "@/components/ui/Skeleton";
import { StateView } from "@/components/ui/StateView";
import { Switch } from "@/components/ui/Switch";
import { problemMessages } from "@/lib/notifications/types";
import type { SavedSearchView } from "@/lib/search/types";
import "./search.css";

type Load = { state: "loading" } | { state: "denied" } | { state: "error" } | { state: "ready"; rows: SavedSearchView[] };

/**
 * The person's saved searches (spec 012 US7): each opens its results, its daily alert turns on or off at once, and it
 * can be removed. Only the person's own searches are listed; the API decides that and the package limit.
 */
export function SavedSearchList() {
  const t = useTranslations("search.saved");
  const format = useFormatter();
  const [load, setLoad] = useState<Load>({ state: "loading" });
  const [message, setMessage] = useState<{ error: boolean; text: string } | null>(null);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/search/saved");
        if (!live) return;
        if (response.ok) setLoad({ state: "ready", rows: (await response.json()) as SavedSearchView[] });
        else setLoad({ state: response.status === 403 ? "denied" : "error" });
      } catch {
        if (live) setLoad({ state: "error" });
      }
    })();
    return () => {
      live = false;
    };
  }, []);

  async function change(row: SavedSearchView, alerts: boolean) {
    setMessage(null);
    try {
      const response = await fetch(`/api/v1/search/saved/${encodeURIComponent(row.id)}/alerts`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ alerts }),
      });
      if (response.ok) {
        const view = (await response.json()) as SavedSearchView;
        setLoad((l) => (l.state === "ready" ? { state: "ready", rows: l.rows.map((r) => (r.id === view.id ? view : r)) } : l));
        setMessage({ error: false, text: t(alerts ? "alertsOn" : "alertsOff", { query: row.query }) });
      } else {
        setMessage({ error: true, text: (await problemMessages(response)).join(" ") || t("failed") });
      }
    } catch {
      setMessage({ error: true, text: t("failed") });
    }
  }

  async function remove(row: SavedSearchView) {
    setMessage(null);
    try {
      const response = await fetch(`/api/v1/search/saved/${encodeURIComponent(row.id)}`, { method: "DELETE" });
      if (response.ok) {
        setLoad((l) => (l.state === "ready" ? { state: "ready", rows: l.rows.filter((r) => r.id !== row.id) } : l));
        setMessage({ error: false, text: t("removed", { query: row.query }) });
      } else {
        setMessage({ error: true, text: (await problemMessages(response)).join(" ") || t("failed") });
      }
    } catch {
      setMessage({ error: true, text: t("failed") });
    }
  }

  if (load.state === "loading") return <Skeleton lines={["2.5rem", "2.5rem", "2.5rem"]} />;
  if (load.state === "denied") return <StateView kind="denied" title={t("deniedTitle")} message={t("denied")} />;
  if (load.state === "error") return <StateView kind="error" title={t("errorTitle")} message={t("failed")} />;

  const columns: Column<SavedSearchView>[] = [
    {
      key: "query",
      header: t("query"),
      rowHeader: true,
      cell: (r) => (
        <Link href={`/search?${new URLSearchParams({ q: r.query })}`}>
          <bdi>{r.query}</bdi>
        </Link>
      ),
    },
    {
      key: "alerts",
      header: t("alerts"),
      cell: (r) => (
        <Switch label={t("alertsLabel", { query: r.query })} checked={r.alerts} onChange={(e) => void change(r, e.currentTarget.checked)} />
      ),
    },
    {
      key: "alerted",
      header: t("alerted"),
      priority: "low",
      cell: (r) => (r.alertedAt ? <time dateTime={r.alertedAt}>{format.dateTime(new Date(r.alertedAt), { dateStyle: "medium" })}</time> : t("never")),
    },
    {
      key: "remove",
      header: t("actions"),
      align: "end",
      cell: (r) => (
        <Button variant="quiet" size="sm" onClick={() => void remove(r)} aria-label={t("removeLabel", { query: r.query })}>
          {t("remove")}
        </Button>
      ),
    },
  ];

  return (
    <div className="search-saved" data-testid="saved-searches">
      <DataTable
        caption={t("caption")}
        columns={columns}
        rows={load.rows}
        rowKey={(r) => r.id}
        emptyTitle={t("emptyTitle")}
        emptyMessage={t("empty")}
      />
      <div aria-live="polite">
        {message ? (
          <p className={message.error ? "field-error" : "search-count"} role={message.error ? "alert" : "status"}>
            {message.text}
          </p>
        ) : null}
      </div>
    </div>
  );
}
