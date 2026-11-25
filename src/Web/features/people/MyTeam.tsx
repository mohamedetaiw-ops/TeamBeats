"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import type { TeamPage } from "@/lib/people/types";
import { Widget } from "@/components/ui/Widget";
import { PeopleList } from "@/components/ui/Person";
import { PersonItem } from "./PersonItem";
import "./people.css";

/** My team (spec 010 US3): direct reports, or the whole branch below the viewer, paged for very large teams. */
export function MyTeam({ initial }: { initial: TeamPage }) {
  const t = useTranslations("people");
  const locale = useLocale();
  const [indirect, setIndirect] = useState(false);
  const [page, setPage] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);

  async function load(withIndirect: boolean, offset: number) {
    setBusy(true);
    setError(false);
    try {
      const response = await fetch(`/api/v1/me/team?indirect=${withIndirect}&offset=${offset}`);
      if (!response.ok) {
        setError(true);
        return;
      }
      const next = (await response.json()) as TeamPage;
      setPage((before) => (offset > 0 ? { ...next, items: [...before.items, ...next.items] } : next));
    } catch {
      setError(true);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Widget
      title={t("team.total", { count: page.total })}
      action={
        <label className="people-check">
          <input type="checkbox" checked={indirect} onChange={(e) => {
              setIndirect(e.target.checked);
              void load(e.target.checked, 0);
            }} />
          {t("team.indirect")}
        </label>
      }
    >
      <p className="visually-hidden" role="status">
        {t("team.total", { count: page.total })}
      </p>
      {error ? <p className="notice notice-error">{t("failed")}</p> : null}
      <PeopleList layout="cards" testId="team-list">
        {page.items.map((p) => (
          <li key={p.id}>
            <PersonItem form="card" person={p} locale={locale} extra={indirect && p.level ? t("team.level", { level: p.level }) : undefined} />
          </li>
        ))}
      </PeopleList>
      {page.next !== null ? (
        <Button busy={busy} busyLabel={t("loading")} onClick={() => void load(indirect, page.next ?? 0)}>
          {t("team.more")}
        </Button>
      ) : null}
    </Widget>
  );
}
