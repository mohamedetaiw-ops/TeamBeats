"use client";

import Link from "next/link";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import { engagementApi, recognitionHref, type LeaderboardView } from "@/lib/engagement";
import { useHomeAnswer } from "@/lib/engagementHome";
import { BarList } from "@/components/ui/Charts";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import "@/features/services/home.css";

/**
 * The leaderboard card (spec 008 FR-055, spec 023 T015): the people with the most points this month. The API leaves out
 * people who opted out and answers not found when points or the leaderboard are off, which shows as an empty card.
 */
export default function LeaderboardCard({ props }: PlacementProps) {
  const t = useTranslations("engagement.home");
  const locale = useLocale();
  const format = useFormatter();
  const top = typeof props.top === "number" && props.top >= 1 && props.top <= 10 ? Math.floor(props.top) : 5;
  const title = localized(props.title, locale) || t("leaderboardTitle");
  const { answer, retry } = useHomeAnswer<LeaderboardView>(`${engagementApi}/leaderboard?period=month`);

  const shell = (body: React.ReactNode, busy = false) => (
    <section className="home-card" aria-labelledby="home-leaderboard-title" aria-busy={busy || undefined}>
      <div className="home-card-head">
        <h2 id="home-leaderboard-title">{title}</h2>
        <Link href={recognitionHref}>{t("leaderboardAll")}</Link>
      </div>
      {body}
    </section>
  );

  if (answer === "loading") return shell(<StateView kind="loading" minBlockSize="8rem" />, true);
  if (answer === "failed") return shell(<StateView kind="error" title={t("failed")} message="" action={<Button onClick={retry}>{t("retry")}</Button>} />);
  const entries = answer === "off" ? [] : answer.entries.slice(0, top);
  if (entries.length === 0) return shell(<StateView kind="empty" title={t("leaderboardEmpty")} message="" icon="trend-up" />);
  return shell(
    <BarList
      label={t("leaderboardLabel")}
      items={entries.map((e) => ({
        key: e.person.id,
        label: `${format.number(e.rank)}. ${e.person.displayName}`,
        value: e.points,
        valueText: t("points", { count: e.points }),
      }))}
    />,
  );
}
