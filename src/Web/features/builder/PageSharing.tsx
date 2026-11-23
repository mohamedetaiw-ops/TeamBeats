"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { SendMessages, useSend } from "@/features/builder/manage/useSend";

type Share = { orgUnitId: string; name: string };

function load(pageId: string): Promise<Share[] | null> {
  return fetch(`/api/v1/experience/shares/page/${pageId}`, { cache: "no-store" })
    .then(async (r) => (r.ok ? ((await r.json()) as Share[]) : null))
    .catch(() => null);
}

/**
 * Sharing an organization's page with another organization (spec 033 R2 T019, FR-021). The page reaches its own branch
 * already; a share adds another organization and those below it. People there see the page with its owner named and keep
 * their own permissions. The panel shows only when the API lets the editor manage sharing; the API checks every change.
 */
export function PageSharing({ pageId, units }: { pageId: string; units: OrgUnit[] }) {
  const t = useTranslations("builder.sharing");
  const locale = useLocale();
  const selectId = useId();
  const [shares, setShares] = useState<Share[] | null>(null);
  const [chosen, setChosen] = useState("");
  const { busy, result, send } = useSend(t("failed"));

  useEffect(() => {
    let live = true;
    void load(pageId).then((list) => {
      if (live) setShares(list);
    });
    return () => {
      live = false;
    };
  }, [pageId]);

  if (shares === null) return null;
  const offered = units.filter((u) => u.status === "active" && !shares.some((s) => s.orgUnitId === u.id));

  async function share() {
    const unit = units.find((u) => u.id === chosen);
    if (!unit) return;
    if ((await send(`/api/v1/experience/shares/page/${pageId}/${unit.id}`, "PUT", null, t("shared", { organization: unitName(unit, locale) }))) !== null) {
      setChosen("");
      setShares(await load(pageId));
    }
  }

  async function unshare(s: Share) {
    if ((await send(`/api/v1/experience/shares/page/${pageId}/${s.orgUnitId}`, "DELETE", null, t("unshared", { organization: s.name }))) !== null) {
      setShares(await load(pageId));
    }
  }

  return (
    <section className="builder-sharing" aria-labelledby={`${selectId}-title`} data-testid="page-sharing">
      <h2 id={`${selectId}-title`}>{t("title")}</h2>
      <p className="notice">{t("intro")}</p>
      {shares.length === 0 ? (
        <p className="notice">{t("none")}</p>
      ) : (
        <ul className="builder-manage-list builder-sharing-list">
          {shares.map((s) => (
            <li key={s.orgUnitId} className="builder-revision">
              <bdi>{s.name}</bdi>
              <Button variant="secondary" disabled={busy} onClick={() => void unshare(s)} aria-label={t("removeFor", { organization: s.name })}>
                {t("remove")}
              </Button>
            </li>
          ))}
        </ul>
      )}
      <div className="builder-manage-inline">
        <label htmlFor={selectId}>{t("organization")}</label>
        <select id={selectId} value={chosen} onChange={(e) => setChosen(e.target.value)}>
          <option value="">{t("choose")}</option>
          {offered.map((u) => (
            <option key={u.id} value={u.id}>
              {unitName(u, locale)}
            </option>
          ))}
        </select>
        <Button variant="primary" disabled={busy || chosen === ""} onClick={() => void share()}>
          {t("share")}
        </Button>
      </div>
      <SendMessages result={result} />
    </section>
  );
}
