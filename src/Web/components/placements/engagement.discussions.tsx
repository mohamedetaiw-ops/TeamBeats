"use client";

import Link from "next/link";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import { communityHref, engagementApi, isGuid, postHref, type FeedPage } from "@/lib/engagement";
import { snippet, useHomeAnswer } from "@/lib/engagementHome";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import "@/features/services/home.css";

/**
 * The latest discussions card (spec 023 widgets for 008, FR-022): the newest posts of one community, read through the
 * same feed query as the community page, so a community the viewer cannot read shows as empty.
 */
export default function DiscussionsCard({ props }: PlacementProps) {
  const t = useTranslations("engagement.home");
  const locale = useLocale();
  const format = useFormatter();
  const community = typeof props.community === "string" && isGuid(props.community) ? props.community : "";
  const top = typeof props.top === "number" && props.top >= 1 && props.top <= 10 ? Math.floor(props.top) : 3;
  const title = localized(props.title, locale) || t("discussionsTitle");
  const { answer, retry } = useHomeAnswer<FeedPage>(community ? `${engagementApi}/communities/${community}/posts?limit=${top}` : "");

  const shell = (body: React.ReactNode, busy = false) => (
    <Widget title={title} busy={busy} action={community ? <Link href={communityHref(community)}>{t("discussionsAll")}</Link> : null}>
      {body}
    </Widget>
  );

  if (!community) return shell(<StateView kind="empty" title={t("discussionsEmpty")} message="" icon="chat" />);
  if (answer === "loading") return shell(<StateView kind="loading" minBlockSize="8rem" />, true);
  if (answer === "failed") return shell(<StateView kind="error" title={t("failed")} message="" action={<Button onClick={retry}>{t("retry")}</Button>} />);
  const posts = answer === "off" ? [] : answer.items.filter((p) => p.status === "published").slice(0, top);
  if (posts.length === 0) return shell(<StateView kind="empty" title={t("discussionsEmpty")} message="" icon="chat" />);
  return shell(
    <ul className="home-list" aria-label={title}>
      {posts.map((p) => (
        <li key={p.id}>
          <Link href={postHref(p.communityId, p.id)} dir="auto">
            {snippet(p.body) || t("discussionsOpen")}
          </Link>
          <span className="home-meta">
            <bdi>{p.anonymous ? t("anonymous") : p.author.displayName}</bdi>
            {" · "}
            <time dateTime={p.publishedAt}>{format.dateTime(new Date(p.publishedAt), { dateStyle: "medium" })}</time>
            {" · "}
            {t("comments", { count: p.commentCount })}
          </span>
        </li>
      ))}
    </ul>,
  );
}
