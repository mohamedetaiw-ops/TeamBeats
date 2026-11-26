"use client";

import { useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { localName } from "@/lib/people/types";
import type { ExpertsView, Proficiency } from "@/lib/people/skills";
import { PersonItem } from "./PersonItem";
import "./people.css";

const tones: Record<Proficiency, "accent" | "success" | "neutral"> = { expert: "accent", good: "success", basic: "neutral" };

/** Find an expert (spec 010 R2 US6 scenario 3): people with a matching skill, strongest first, as the API ranked them. */
export function ExpertFinder({ initial, query }: { initial: ExpertsView; query: string }) {
  const t = useTranslations("people.experts");
  const level = useTranslations("people.skills.level");
  const count = useTranslations("people.skills");
  const locale = useLocale();
  if (!initial.enabled) {
    return <StateView kind="unsupported" title={t("offTitle")} message={t("off")} />;
  }
  return (
    <div className="people">
      <form className="people-search" role="search" action="/people/experts">
        <FormField label={t("search")} name="q" type="search" defaultValue={query} hint={t("searchHint")} dir="auto" />
        <Button type="submit" variant="primary" icon="search">
          {t("find")}
        </Button>
      </form>
      {query.trim().length < 2 ? (
        <StateView kind="empty" title={t("startTitle")} message={t("start")} icon="search" />
      ) : initial.items.length === 0 ? (
        <StateView kind="empty" title={t("noneTitle")} message={t("none")} icon="people" />
      ) : (
        <ul className="people-experts" data-testid="experts" aria-label={t("results")}>
          {initial.items.map((e) => (
            <li key={e.person.id}>
              <PersonItem person={e.person} locale={locale} />
              <span className="people-expert-skill">
                <bdi>{localName(e.skillNames, locale)}</bdi>
                <Badge tone={tones[e.proficiency]}>{level(e.proficiency)}</Badge>
                <span className="people-meta">{count("endorsements", { count: e.endorsements })}</span>
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
