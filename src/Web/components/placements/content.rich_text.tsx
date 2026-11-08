"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";
import { readHref, type ReaderView } from "@/lib/content/types";
import { BodyView } from "@/features/content/BodyView";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import "@/features/content/content.css";

type Answer = "loading" | "failed" | "missing" | ReaderView;

/**
 * The rich text component (spec 009 FR-030): one live item shown in place. An item the viewer may not read answers not
 * found, and the component then shows nothing about it.
 */
export default function RichText({ props }: PlacementProps) {
  const t = useTranslations("content");
  const itemId = typeof props.itemId === "string" ? props.itemId : "";
  const showTitle = props.showTitle !== false;
  const [answer, setAnswer] = useState<Answer>("loading");
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!itemId) return;
    let live = true;
    fetch(`/api/v1/content/items/${encodeURIComponent(itemId)}/view`)
      .then(async (r) => {
        if (r.status === 404 || r.status === 403) {
          if (live) setAnswer("missing");
          return;
        }
        if (!r.ok) throw new Error(String(r.status));
        const view = (await r.json()) as ReaderView;
        if (live) setAnswer(view);
      })
      .catch(() => live && setAnswer("failed"));
    return () => {
      live = false;
    };
  }, [itemId, attempt]);

  if (!itemId || answer === "missing") return <StateView kind="empty" title={t("component.richText.missing")} message="" />;
  if (answer === "loading") return <StateView kind="loading" minBlockSize="8rem" />;
  if (answer === "failed") {
    const retry = (
      <Button
        onClick={() => {
          setAnswer("loading");
          setAttempt((a) => a + 1);
        }}
      >
        {t("component.richText.retry")}
      </Button>
    );
    return <StateView kind="error" title={t("component.richText.failed")} message="" action={retry} />;
  }
  return (
    <article className="content-card" lang={answer.locale} aria-label={answer.title}>
      {showTitle ? (
        <h2>
          <Link href={readHref(answer.id)}>
            <bdi>{answer.title}</bdi>
          </Link>
        </h2>
      ) : null}
      <BodyView body={answer.body} embedTitle={t("read.embed")} />
    </article>
  );
}
