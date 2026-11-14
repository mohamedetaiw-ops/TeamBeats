"use client";

import Link from "next/link";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import { engagementApi, localName, recognitionHref, type RecognitionPage } from "@/lib/engagement";
import { useHomeAnswer } from "@/lib/engagementHome";
import { Avatar } from "@/components/ui/Avatar";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import "@/features/services/home.css";
import "@/features/engagement/engagement.css";

/**
 * The recognition card (spec 008 FR-055, spec 023 T015): the newest thank-you shout-outs from the recognition wall,
 * with a link to the wall. The API returns only recognitions the viewer may see.
 */
export default function RecognitionCard({ props }: PlacementProps) {
  const t = useTranslations("engagement.home");
  const locale = useLocale();
  const format = useFormatter();
  const top = typeof props.top === "number" && props.top >= 1 && props.top <= 10 ? Math.floor(props.top) : 3;
  const title = localized(props.title, locale) || t("recognitionTitle");
  const { answer, retry } = useHomeAnswer<RecognitionPage>(`${engagementApi}/recognitions`);

  const shell = (body: React.ReactNode, busy = false) => (
    <Widget title={title} busy={busy} action={<Link href={recognitionHref}>{t("recognitionAll")}</Link>}>
      {body}
    </Widget>
  );

  if (answer === "loading") return shell(<StateView kind="loading" minBlockSize="8rem" />, true);
  if (answer === "failed") return shell(<StateView kind="error" title={t("failed")} message="" action={<Button onClick={retry}>{t("retry")}</Button>} />);
  const items = answer === "off" ? [] : answer.items.slice(0, top);
  if (items.length === 0) return shell(<StateView kind="empty" title={t("recognitionEmpty")} message="" icon="star" />);
  return shell(
    <ul className="home-list" aria-label={title}>
      {items.map((r) => {
        const to = r.team ? localName(r.team.names, locale) : r.recipients.map((p) => p.displayName).join(", ");
        return (
          <li key={r.id}>
            <span className="engagement-row">
              <Avatar name={r.giver.displayName} size="sm" />
              <span>{t.rich("recognitionLine", { giverName: r.giver.displayName, toName: to, giver: (c) => <bdi>{c}</bdi>, to: (c) => <bdi>{c}</bdi> })}</span>
            </span>
            <span dir="auto">{r.message}</span>
            <span className="home-meta">
              {r.value ? <Badge tone="accent">{localName(r.value.names, locale)}</Badge> : null}{" "}
              <time dateTime={r.createdAt}>{format.dateTime(new Date(r.createdAt), { dateStyle: "medium" })}</time>
            </span>
          </li>
        );
      })}
    </ul>,
  );
}
