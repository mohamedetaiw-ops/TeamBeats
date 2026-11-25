"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import { pick, type CatalogueView, type Category, type ServiceCard } from "@/lib/services/types";
import { serviceLook } from "@/lib/services/look";
import { Button } from "@/components/ui/Button";
import { LaunchTiles } from "@/components/ui/LaunchTiles";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import "@/features/services/home.css";

type Answer = "loading" | "failed" | { shown: ServiceCard[]; total: number; categories: Category[] };

/** Favourites first, then recently used, then the rest of the catalogue in its own order, without repeats. */
function choose(view: CatalogueView, top: number): ServiceCard[] {
  const byId = new Map(view.services.map((s) => [s.id, s]));
  const ordered = [
    ...view.services.filter((s) => s.favourite),
    ...view.recent.map((id) => byId.get(id)).filter((s): s is ServiceCard => Boolean(s)),
    ...view.services,
  ];
  const seen = new Set<string>();
  return ordered.filter((s) => !seen.has(s.id) && seen.add(s.id)).slice(0, top);
}

/**
 * The My services strip (spec 016 home component, spec 008 scope): the services the viewer uses most as tiles, and a
 * link to the whole catalogue. The catalogue API lists only services the viewer's audience may use.
 */
export default function MyServices({ props }: PlacementProps) {
  const t = useTranslations("services.home.services");
  const tc = useTranslations("services.catalogue");
  const locale = useLocale();
  const top = typeof props.top === "number" && props.top >= 1 && props.top <= 10 ? Math.floor(props.top) : 5;
  const title = localized(props.title, locale) || t("title");
  const [answer, setAnswer] = useState<Answer>("loading");
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let live = true;
    fetch("/api/v1/services")
      .then(async (r) => {
        if (!r.ok) throw new Error(String(r.status));
        const view = (await r.json()) as CatalogueView;
        if (live) setAnswer({ shown: choose(view, top), total: view.services.length, categories: view.categories });
      })
      .catch(() => live && setAnswer("failed"));
    return () => {
      live = false;
    };
  }, [top, attempt]);

  const shell = (body: React.ReactNode, total: number | null, busy = false) => (
    <Widget title={title} busy={busy} testId="home-my-services" action={<Link href="/services">{total ? t("all", { count: total }) : t("allPlain")}</Link>}>
      {body}
    </Widget>
  );

  if (answer === "loading") return shell(<StateView kind="loading" minBlockSize="6rem" />, null, true);
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
    return shell(<StateView kind="error" title={t("failed")} message="" action={retry} />, null);
  }
  if (answer.shown.length === 0) return shell(<StateView kind="empty" title={t("none")} message="" />, answer.total);
  return shell(
    <LaunchTiles
      tiles={answer.shown.map((s) => {
        const look = serviceLook(s, answer.categories);
        const linked = s.kind === "linked" && s.url;
        return {
          id: s.id,
          title: pick(s.names, locale),
          icon: look.icon,
          tone: look.tone,
          href: linked ? s.url! : s.kind === "module" && s.launchRoute ? s.launchRoute : `/services/${s.id}`,
          external: linked ? { label: tc("external") } : undefined,
          onOpen: linked ? () => void fetch(`/api/v1/services/${s.id}/used`, { method: "POST" }) : undefined,
        };
      })}
    />,
    answer.total,
  );
}
