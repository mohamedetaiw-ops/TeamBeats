import { useLocale, useTranslations } from "next-intl";
import { WorkItems } from "@/components/ui/WorkItems";
import { isolate } from "@/lib/bidi";
import { dateTimeFull, joinMeta, relativeTime } from "@/lib/format";
import { requestTone } from "@/lib/services/status";
import { pick, type RequestSummary } from "@/lib/services/types";

/**
 * Requests as shared work item rows (spec 036 DLS-061): the service name, a status badge, one meta line (number ·
 * current step · who it waits for · who it is for) and the last change. Everything shown comes from the request
 * summary the API returned.
 */
export function WorkItemList({ items, now, person, testId }: { items: RequestSummary[]; now: number; person: "onBehalfOf" | "requester"; testId: string }) {
  const t = useTranslations("services");
  const locale = useLocale();
  return (
    <WorkItems
      testId={testId}
      items={items.map((r) => {
        const who = person === "requester" ? (r.onBehalfOf ?? r.requester) : r.onBehalfOf;
        const changed = r.closedAt ?? r.submittedAt ?? r.createdAt;
        // While a request is open the row names who it waits for (spec 016 FR-022).
        const owner = r.closedAt === null && r.assignees.length > 0 ? t("requests.waitingFor", { name: isolate(r.assignees[0]!.displayName), others: r.assignees.length - 1 }) : null;
        return {
          id: r.id,
          href: `/requests/${r.id}`,
          title: pick(r.serviceNames, locale),
          number: r.number,
          rest: joinMeta([r.currentStep ? pick(r.currentStep, locale) : null, owner, who ? t("team.for", { name: isolate(who.displayName) }) : null]),
          status: { text: t(`status.${r.status}`), tone: requestTone(r.status) },
          changed: { dateTime: changed, full: dateTimeFull(changed, locale), text: relativeTime(changed, locale, now) },
        };
      })}
    />
  );
}
