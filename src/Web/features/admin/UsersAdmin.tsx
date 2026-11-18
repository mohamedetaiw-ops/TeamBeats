"use client";

import Link from "next/link";
import { useState } from "react";
import { useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { SearchField } from "@/components/ui/Inputs";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import type { UserSummary } from "@/lib/admin/types";
import { useWhen } from "./shared";

const FILTERS = ["all", "active", "disabled", "never"] as const;
type Filter = (typeof FILTERS)[number];

export function userState(u: { status: string; membershipStatus: string | null }): "active" | "disabled" {
  return u.status === "active" && (u.membershipStatus ?? "active") === "active" ? "active" : "disabled";
}

/**
 * The people of the workspace (spec 006 FR-009): name, email, whether they can sign in and when they last did. The
 * API returns only the people the administrator's scope covers; a search goes back to the API.
 */
export function UsersAdmin({ initial }: { initial: UserSummary[] }) {
  const t = useTranslations("admin.users");
  const when = useWhen();
  const [users, setUsers] = useState(initial);
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState<Filter>("all");
  const [searching, setSearching] = useState(false);

  async function search(q: string) {
    setSearching(true);
    try {
      const response = await fetch(`/api/v1/admin/users${q.trim() ? `?q=${encodeURIComponent(q.trim())}` : ""}`, { cache: "no-store" });
      if (response.ok) setUsers((await response.json()) as UserSummary[]);
    } finally {
      setSearching(false);
    }
  }

  const rows = users.filter((u) => (filter === "all" ? true : filter === "never" ? !u.lastSignInAt : userState(u) === filter));
  const count = (f: Filter) => users.filter((u) => (f === "all" ? true : f === "never" ? !u.lastSignInAt : userState(u) === f)).length;

  return (
    <div className="admin-screen">
      <DataTable
        caption={t("caption")}
        rows={rows}
        rowKey={(u) => u.id}
        emptyTitle={t("empty")}
        emptyMessage={t("emptyHint")}
        defaultSort={{ key: "name", direction: "ascending" }}
        toolbar={
          <div className="admin-toolbar">
            <form
              role="search"
              className="ui-control-row"
              onSubmit={(e) => {
                e.preventDefault();
                void search(query);
              }}
            >
              <SearchField label={t("search")} name="q" value={query} onValueChange={setQuery} />
              <Button type="submit" busy={searching} busyLabel={t("searching")}>
                {t("find")}
              </Button>
            </form>
            <SegmentedControl
              label={t("filter")}
              name="user-filter"
              value={filter}
              options={FILTERS.map((f) => ({ value: f, label: t("filters", { filter: f, count: count(f) }) }))}
              onChange={(f) => setFilter(f as Filter)}
            />
          </div>
        }
        columns={[
          {
            key: "name",
            header: t("name"),
            rowHeader: true,
            sortValue: (u) => u.displayName,
            cell: (u) => (
              <Link href={`/admin/users/${u.id}`}>
                <bdi>{u.displayName}</bdi>
              </Link>
            ),
          },
          { key: "email", header: t("email"), sortValue: (u) => u.email, cell: (u) => <bdi dir="ltr">{u.email}</bdi>, hideable: true },
          {
            key: "status",
            header: t("status"),
            sortValue: (u) => userState(u),
            cell: (u) => <Badge tone={userState(u) === "active" ? "success" : "danger"}>{t(`states.${userState(u)}`)}</Badge>,
          },
          { key: "last", header: t("lastSignIn"), sortValue: (u) => u.lastSignInAt ?? "", cell: (u) => when(u.lastSignInAt, "short"), hideable: true, priority: "low" },
        ]}
      />
    </div>
  );
}
