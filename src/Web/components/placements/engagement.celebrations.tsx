"use client";

import Link from "next/link";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import { celebrationsHref, engagementApi, type CelebrationEntry, type CelebrationsView } from "@/lib/engagement";
import { useHomeAnswer } from "@/lib/engagementHome";
import { Avatar } from "@/components/ui/Avatar";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import "@/features/services/home.css";
import "@/features/engagement/engagement.css";

/**
 * The celebrations card (spec 008 FR-055, spec 023 T015): birthdays, work anniversaries and new joiners in the
 * viewer's units, today first and then the next few days, with a link to the celebrations page to send a greeting.
 * What shows follows the tenant's celebration settings and each person's choices, which the API applies.
 */
export default function CelebrationsCard({ props }: PlacementProps) {
  const t = useTranslations("engagement.home");
  const k = useTranslations("engagement.celebrations");
  const locale = useLocale();
  const format = useFormatter();
  const top = typeof props.top === "number" && props.top >= 1 && props.top <= 10 ? Math.floor(props.top) : 4;
  const title = localized(props.title, locale) || t("celebrationsTitle");
  const { answer, retry } = useHomeAnswer<CelebrationsView>(`${engagementApi}/celebrations`);

  const shell = (body: React.ReactNode, busy = false) => (
    <section className="home-card" aria-labelledby="home-celebrations-title" aria-busy={busy || undefined}>
      <div className="home-card-head">
        <h2 id="home-celebrations-title">{title}</h2>
        <Link href={celebrationsHref}>{t("celebrationsAll")}</Link>
      </div>
      {body}
    </section>
  );

  if (answer === "loading") return shell(<StateView kind="loading" minBlockSize="8rem" />, true);
  if (answer === "failed") return shell(<StateView kind="error" title={t("failed")} message="" action={<Button onClick={retry}>{t("retry")}</Button>} />);
  const entries = answer === "off" ? [] : [...answer.today, ...answer.upcoming, ...answer.joiners].slice(0, top);
  if (entries.length === 0) return shell(<StateView kind="empty" title={t("celebrationsEmpty")} message="" icon="calendar" />);
  const when = (e: CelebrationEntry) => {
    if (e.today) return t("today");
    const [, month = 1, day = 1] = e.on.split("-").map(Number);
    return format.dateTime(new Date(Date.UTC(2024, month - 1, day, 12)), { month: "long", day: "numeric", timeZone: "UTC" });
  };
  return shell(
    <ul className="home-list" aria-label={title}>
      {entries.map((e) => (
        <li key={`${e.kind}-${e.person.id}`}>
          <span className="engagement-row">
            <Avatar name={e.person.displayName} size="sm" />
            <bdi>{e.person.displayName}</bdi>
            <Badge tone={e.today ? "accent" : "neutral"}>{e.kind === "anniversary" ? k("anniversary", { years: e.years ?? 1 }) : k(`kinds.${e.kind}`)}</Badge>
          </span>
          <span className="home-meta">{when(e)}</span>
        </li>
      ))}
    </ul>,
  );
}
