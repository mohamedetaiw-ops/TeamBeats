"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";
import { initials, localName, personName, type PersonCard, type SearchPage } from "@/lib/people/types";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import "@/features/people/people.css";

type Answer = "loading" | "failed" | PersonCard[];

/** The address the component reads for its source; null when the source needs a unit or people that are not set. */
function sourceUrl(props: PlacementProps["props"], top: number): string | null {
  if (props.source === "unit") {
    return typeof props.unitId === "string" && props.unitId ? `/api/v1/people?unitId=${encodeURIComponent(props.unitId)}&limit=${top}` : null;
  }
  if (props.source === "named") {
    const ids = Array.isArray(props.people) ? props.people.filter((p): p is string => typeof p === "string" && p.length > 0) : [];
    return ids.length > 0 ? `/api/v1/people?ids=${ids.map(encodeURIComponent).join(",")}&limit=${top}` : null;
  }
  return `/api/v1/people/newcomers?limit=${top}`;
}

/**
 * The people component (spec 010 US4, FR-010): new joiners, a unit and its subtree, or a named set. The directory API
 * applies the visibility policy on every render, so the component only shows what it gets back.
 */
export default function PeopleList({ props }: PlacementProps) {
  const t = useTranslations("people.component");
  const locale = useLocale();
  const top = typeof props.top === "number" && props.top >= 1 && props.top <= 20 ? Math.floor(props.top) : 6;
  const url = sourceUrl(props, top);
  const [answer, setAnswer] = useState<Answer>("loading");
  const [attempt, setAttempt] = useState(0);
  const title = props.source === "unit" ? t("unit") : props.source === "named" ? t("named") : t("newcomers");

  useEffect(() => {
    if (!url) return;
    let live = true;
    fetch(url)
      .then(async (r) => {
        if (!r.ok) throw new Error(String(r.status));
        const body = (await r.json()) as SearchPage | PersonCard[];
        if (live) setAnswer(Array.isArray(body) ? body : body.items);
      })
      .catch(() => live && setAnswer("failed"));
    return () => {
      live = false;
    };
  }, [url, attempt]);

  const shell = (body: React.ReactNode, busy = false) => (
    <Widget title={title} busy={busy}>
      {body}
    </Widget>
  );

  if (!url) return shell(<StateView kind="empty" title={t("notSet")} message="" />);
  if (answer === "loading") return shell(<StateView kind="loading" minBlockSize="8rem" />, true);
  if (answer === "failed") {
    const retry = (
      <Button
        onClick={() => {
          setAnswer("loading");
          setAttempt((a) => a + 1);
        }}
      >
        {t("retry")}
      </Button>
    );
    return shell(<StateView kind="error" title={t("failed")} message="" action={retry} />);
  }
  if (answer.length === 0) return shell(<StateView kind="empty" title={t("none")} message="" />);
  return shell(
    <ul className="people-list">
      {answer.map((p) => {
        const name = personName(p, locale);
        return (
          <li key={p.id} className="people-item">
            <span className="people-avatar" aria-hidden="true">
              {initials(name)}
            </span>
            <span className="people-item-text">
              <Link href={`/people/${p.id}`} className="people-item-name">
                <bdi>{name}</bdi>
              </Link>
              {p.jobTitle ? <bdi>{p.jobTitle}</bdi> : null}
              {p.unit ? (
                <span className="people-meta">
                  <bdi>{localName(p.unit.names, locale)}</bdi>
                </span>
              ) : null}
            </span>
          </li>
        );
      })}
    </ul>,
  );
}
