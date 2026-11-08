"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";
import { readHref, type CriticalNotice, type PendingCritical } from "@/lib/content/types";
import { problemMessages } from "@/lib/notifications/types";
import { Button } from "@/components/ui/Button";
import "@/features/content/content.css";

/**
 * The announcement banner component (spec 009 US8): critical items shown as banners that the viewer still has to
 * confirm or may dismiss. The API decides who sees what and records each confirmation; nothing is shown when there is
 * nothing pending or the pending list cannot be read, so a failure never blocks the page.
 */
export default function AnnouncementBanner(_: PlacementProps) {
  const t = useTranslations("content.component.banner");
  const locale = useLocale();
  const [banners, setBanners] = useState<CriticalNotice[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string[]>>({});

  useEffect(() => {
    let live = true;
    fetch("/api/v1/content/critical/pending")
      .then(async (r) => {
        if (!r.ok) return;
        const pending = (await r.json()) as PendingCritical;
        if (live) setBanners(pending.banners);
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, []);

  const act = async (notice: CriticalNotice, action: "acknowledge" | "dismiss") => {
    setBusy(notice.id);
    const response = await fetch(`/api/v1/content/items/${encodeURIComponent(notice.id)}/${action}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: action === "acknowledge" ? JSON.stringify({ locale, channel: "web" }) : undefined,
    }).catch(() => null);
    setBusy(null);
    if (response?.ok) {
      setBanners((list) => list.filter((b) => b.id !== notice.id));
      return;
    }
    const messages = response ? await problemMessages(response) : [];
    setErrors((e) => ({ ...e, [notice.id]: messages.length > 0 ? messages : [t("failed")] }));
  };

  if (banners.length === 0) return null;
  return (
    <div className="content-banners">
      {banners.map((notice) => (
        <section key={notice.id} className="content-banner" role="region" aria-label={notice.title} lang={notice.locale}>
          <h2>
            <Link href={readHref(notice.id)}>
              <bdi>{notice.title}</bdi>
            </Link>
          </h2>
          {notice.summary ? (
            <p className="content-meta">
              <bdi>{notice.summary}</bdi>
            </p>
          ) : null}
          <div className="content-actions">
            {notice.requireAck ? (
              <Button variant="primary" busy={busy === notice.id} busyLabel={t("sending")} onClick={() => act(notice, "acknowledge")}>
                {t("confirm")}
              </Button>
            ) : null}
            {notice.dismissible ? (
              <Button variant="quiet" disabled={busy === notice.id} onClick={() => act(notice, "dismiss")}>
                {t("dismiss")}
              </Button>
            ) : null}
          </div>
          {errors[notice.id]?.length ? (
            <p className="content-meta" role="alert">
              {errors[notice.id]?.join(" ")}
            </p>
          ) : null}
        </section>
      ))}
    </div>
  );
}
