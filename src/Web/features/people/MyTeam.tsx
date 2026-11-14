"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import type { TeamPage } from "@/lib/people/types";
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
    <section aria-labelledby="people-team-list" className="people-section">
      <div className="people-head">
        <h2 id="people-team-list" aria-live="polite">
          {t("team.total", { count: page.total })}
        </h2>
        <label className="people-check">
          <input type="checkbox" checked={indirect} onChange={(e) => {
              setIndirect(e.target.checked);
              void load(e.target.checked, 0);
            }} />
          {t("team.indirect")}
        </label>
      </div>
      {error ? <p className="notice notice-error">{t("failed")}</p> : null}
      <ul className="people-list people-cards" data-testid="team-list">
        {page.items.map((p) => (
          <li key={p.id}>
            <PersonItem form="card" person={p} locale={locale} extra={indirect && p.level ? t("team.level", { level: p.level }) : undefined} />
          </li>
        ))}
      </ul>
      {page.next !== null ? (
        <Button busy={busy} busyLabel={t("loading")} onClick={() => void load(indirect, page.next ?? 0)}>
          {t("team.more")}
        </Button>
      ) : null}
    </section>
  );
}
