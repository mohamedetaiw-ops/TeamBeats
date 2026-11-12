"use client";

import Link from "next/link";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import { awardsHref, engagementApi, localName, type AwardList, type AwardNominee } from "@/lib/engagement";
import { useHomeAnswer } from "@/lib/engagementHome";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import "@/features/services/home.css";

/**
 * The awards card (spec 008 FR-055, spec 023 T015): the newest team and star of the period awards, with their winners
 * once chosen or the day nominations close while they are open, and a link to the awards page.
 */
export default function AwardsCard({ props }: PlacementProps) {
  const t = useTranslations("engagement.home");
  const locale = useLocale();
  const format = useFormatter();
  const top = typeof props.top === "number" && props.top >= 1 && props.top <= 5 ? Math.floor(props.top) : 3;
  const title = localized(props.title, locale) || t("awardsTitle");
  const { answer, retry } = useHomeAnswer<AwardList>(`${engagementApi}/awards`);

  const shell = (body: React.ReactNode, busy = false) => (
    <section className="home-card" aria-labelledby="home-awards-title" aria-busy={busy || undefined}>
      <div className="home-card-head">
        <h2 id="home-awards-title">{title}</h2>
        <Link href={awardsHref}>{t("awardsAll")}</Link>
      </div>
      {body}
    </section>
  );

  if (answer === "loading") return shell(<StateView kind="loading" minBlockSize="8rem" />, true);
  if (answer === "failed") return shell(<StateView kind="error" title={t("failed")} message="" action={<Button onClick={retry}>{t("retry")}</Button>} />);
  const periods = answer === "off" ? [] : answer.periods.slice(0, top);
  if (periods.length === 0) return shell(<StateView kind="empty" title={t("awardsEmpty")} message="" icon="star" />);
  const name = (n: AwardNominee) => n.person?.displayName ?? localName(n.unitNames ?? undefined, locale);
  return shell(
    <ul className="home-list" aria-label={title}>
      {periods.map((p) => (
        <li key={p.id}>
          <bdi>{p.title}</bdi>
          {p.status === "decided" ? (
            <span className="home-meta">
              <Badge tone="success" icon="star">{t("winners")}</Badge> <bdi>{p.winners.map(name).join(", ")}</bdi>
            </span>
          ) : (
            <span className="home-meta">
              {t("nominationsClose", { date: format.dateTime(new Date(`${p.nominationsCloseOn}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }) })}
            </span>
          )}
        </li>
      ))}
    </ul>,
  );
}
