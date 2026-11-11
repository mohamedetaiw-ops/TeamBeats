import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import { Icon } from "@/components/ui/Icon";
import { isolate } from "@/lib/bidi";
import { dateTimeFull, joinMeta, relativeTime } from "@/lib/format";
import { requestTone } from "@/lib/services/status";
import { pick, type RequestSummary } from "@/lib/services/types";

/**
 * The shared work item row for requests (spec 036 DLS-061): the service name as the row's link, a status badge, one
 * meta line (number · current step · who it is for), the last change as a relative time, and a chevron because the row
 * opens a page. Everything shown comes from the request summary the API returned.
 */
export function WorkItemList({ items, now, person, testId }: { items: RequestSummary[]; now: number; person: "onBehalfOf" | "requester"; testId: string }) {
  const t = useTranslations("services");
  const locale = useLocale();
  return (
    <ul className="work-items" data-testid={testId}>
      {items.map((r) => {
        const who = person === "requester" ? (r.onBehalfOf ?? r.requester) : r.onBehalfOf;
        const changed = r.closedAt ?? r.submittedAt ?? r.createdAt;
        const rest = joinMeta([r.currentStep ? pick(r.currentStep, locale) : null, who ? t("team.for", { name: isolate(who.displayName) }) : null]);
        return (
          <li key={r.id} className="work-item">
            <div className="work-item-main">
              <Link className="work-item-title" href={`/requests/${r.id}`}>
                <bdi>{pick(r.serviceNames, locale)}</bdi>
              </Link>
              <span className="work-item-meta">
                <bdi dir="ltr">{r.number}</bdi>
                {rest ? <span className="work-item-rest">{rest}</span> : null}
              </span>
            </div>
            <span className="ui-badge" data-tone={requestTone(r.status)}>
              {t(`status.${r.status}`)}
            </span>
            <time className="work-item-when" dateTime={changed} title={dateTimeFull(changed, locale)}>
              {relativeTime(changed, locale, now)}
            </time>
            <Icon name="chevron-forward" size={16} />
          </li>
        );
      })}
    </ul>
  );
}
