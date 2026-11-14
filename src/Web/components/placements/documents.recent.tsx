"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import {
  documentHref,
  libraryHref,
  type ItemPage,
  type ItemSummary,
} from "@/lib/documents/types";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import "@/features/documents/documents.css";

type Answer = "loading" | "failed" | "missing" | ItemSummary[];

/**
 * The recent documents component (spec 011 plan, spec 032): the newest documents of one library across its folders.
 * The library API checks the viewer's access, so a library they may not open shows nothing about itself.
 */
export default function RecentDocuments({ props }: PlacementProps) {
  const t = useTranslations("documents.component");
  const locale = useLocale();
  const format = useFormatter();
  const library = typeof props.library === "string" ? props.library : "";
  const top =
    typeof props.top === "number" && props.top >= 1 && props.top <= 20
      ? Math.floor(props.top)
      : 5;
  const title = localized(props.title, locale) || t("title");
  const url = `/api/v1/documents/libraries/${encodeURIComponent(library)}/items?recursive=true&sort=modified&desc=true&limit=${top}`;
  const [answer, setAnswer] = useState<Answer>("loading");
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!library) return;
    let live = true;
    fetch(url)
      .then(async (r) => {
        if (r.status === 404 || r.status === 403) {
          if (live) setAnswer("missing");
          return;
        }
        if (!r.ok) throw new Error(String(r.status));
        const page = (await r.json()) as ItemPage;
        if (live) setAnswer(page.items);
      })
      .catch(() => live && setAnswer("failed"));
    return () => {
      live = false;
    };
  }, [library, url, attempt]);

  const shell = (body: React.ReactNode, busy = false) => (
    <Widget
      title={title}
      busy={busy}
      action={library ? <Link href={libraryHref(library)}>{t("all")}</Link> : null}
    >
      {body}
    </Widget>
  );

  if (!library || answer === "missing")
    return shell(<StateView kind="empty" title={t("none")} message="" />);
  if (answer === "loading")
    return shell(<StateView kind="loading" minBlockSize="8rem" />, true);
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
    return shell(
      <StateView kind="error" title={t("failed")} message="" action={retry} />,
    );
  }
  if (answer.length === 0)
    return shell(<StateView kind="empty" title={t("none")} message="" />);
  return shell(
    <ul className="documents-list">
        {answer.map((d) => (
          <li key={d.id}>
            <Link href={documentHref(d.id)}>
              <bdi>{d.name}</bdi>
            </Link>
            {d.versionLabel ? (
              <span className="documents-badge">{d.versionLabel}</span>
            ) : null}
            <span className="documents-meta">
              <time dateTime={d.updatedAt}>
                {format.dateTime(new Date(d.updatedAt), {
                  dateStyle: "medium",
                })}
              </time>
            </span>
          </li>
        ))}
    </ul>,
  );
}
