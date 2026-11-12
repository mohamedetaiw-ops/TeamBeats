"use client";

import Link from "next/link";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import { communityHref, engagementApi, isGuid, type PollView } from "@/lib/engagement";
import { useHomeAnswer } from "@/lib/engagementHome";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import "@/features/services/home.css";

/**
 * The active poll card (spec 023 widgets for 008, FR-022): the newest open poll of one community, read through the
 * same query as the community's polls page, with a link there to vote. Whether results show is the API's decision.
 */
export default function PollCard({ props }: PlacementProps) {
  const t = useTranslations("engagement.home");
  const p = useTranslations("engagement.polls");
  const locale = useLocale();
  const format = useFormatter();
  const community = typeof props.community === "string" && isGuid(props.community) ? props.community : "";
  const title = localized(props.title, locale) || t("pollTitle");
  const { answer, retry } = useHomeAnswer<PollView[]>(community ? `${engagementApi}/communities/${community}/polls` : "");
  const pollsHref = `${communityHref(community)}/polls`;

  const shell = (body: React.ReactNode, busy = false) => (
    <section className="home-card" aria-labelledby={`home-poll-${community}`} aria-busy={busy || undefined}>
      <div className="home-card-head">
        <h2 id={`home-poll-${community}`}>{title}</h2>
        {community ? <Link href={pollsHref}>{t("pollAll")}</Link> : null}
      </div>
      {body}
    </section>
  );

  if (!community) return shell(<StateView kind="empty" title={t("pollEmpty")} message="" icon="trend-up" />);
  if (answer === "loading") return shell(<StateView kind="loading" minBlockSize="8rem" />, true);
  if (answer === "failed") return shell(<StateView kind="error" title={t("failed")} message="" action={<Button onClick={retry}>{t("retry")}</Button>} />);
  const poll = answer === "off" ? undefined : answer.find((x) => !x.closed);
  if (!poll) return shell(<StateView kind="empty" title={t("pollEmpty")} message="" icon="trend-up" />);
  return shell(
    <div className="home-list">
      <p dir="auto">
        <strong>{poll.question}</strong>
      </p>
      <p className="home-meta">
        {p("closesOn", { when: format.dateTime(new Date(poll.closesAt), { dateStyle: "medium", timeStyle: "short" }) })}
        {poll.voters !== null ? ` · ${p("voters", { count: poll.voters })}` : null}
      </p>
      <p>
        {poll.voted ? <Badge tone="success" icon="check">{t("pollVoted")}</Badge> : null}{" "}
        {poll.canVote && !poll.voted ? (
          <ButtonLink size="sm" variant="primary" href={pollsHref}>
            {p("vote")}
          </ButtonLink>
        ) : null}
      </p>
    </div>,
  );
}
