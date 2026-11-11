"use client";

import { useFormatter, useLocale, useTranslations } from "next-intl";
import Link from "next/link";
import { Badge } from "@/components/ui/Badge";
import { pick } from "@/lib/services/types";
import type { JourneySummary } from "@/lib/journeys";
import "./journeys.css";

/** The person's own journeys with progress; each opens its steps. */
export function MyJourneys({ journeys }: { journeys: JourneySummary[] }) {
  const t = useTranslations("journeys.mine");
  const locale = useLocale();
  const format = useFormatter();
  const date = (d: string) => format.dateTime(new Date(`${d}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
  if (journeys.length === 0) {
    return <p className="field-hint">{t("empty")}</p>;
  }
  return (
    <section className="journeys-section" aria-labelledby="my-journeys">
      <h2 id="my-journeys">{t("title")}</h2>
      <ul className="journeys-list">
        {journeys.map((j) => (
          <li key={j.id} className="journeys-item">
            <Link href={`/hr/journeys/${j.id}`}>
              <bdi>{pick(j.templateNames, locale, j.templateKey)}</bdi>
            </Link>
            <span>
              <Badge tone={j.status === "completed" ? "success" : j.status === "active" ? "info" : "neutral"}>{t(`statuses.${j.status}`)}</Badge>{" "}
              <span className="field-hint">{t("progress", { done: j.done, steps: j.steps, date: date(j.anchorDate) })}</span>
            </span>
          </li>
        ))}
      </ul>
    </section>
  );
}
