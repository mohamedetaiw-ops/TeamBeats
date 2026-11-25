import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { LocationSignageFeed } from "@/lib/notifications/types";
import { ScreenRefresh } from "@/features/notifications/ScreenRefresh";
import { TileCard } from "@/components/ui/TileCard";
import "@/features/content/content.css";

export const dynamic = "force-dynamic";

// A screen at a location opens this address without signing in; the page is never indexed and sends no referrer.
export const metadata: Metadata = { referrer: "no-referrer", robots: { index: false, follow: false } };

/**
 * A location signage screen (spec 015 US7): the live Public announcements marked for signage that target the screen's
 * place, read only, from the address its location administrator made. The API resolves the tenant from the host; an
 * unknown, replaced or revoked address reads as not found, and the page asks again every 30 seconds.
 */
export default async function ScreenPage({ params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;
  if (!/^[A-Za-z0-9_-]{20,100}$/.test(token)) {
    notFound();
  }
  const feed = await apiGet<LocationSignageFeed>(`/api/v1/screens/${encodeURIComponent(token)}`);
  if (!feed.ok) {
    notFound();
  }
  const t = await getTranslations("notifications.screen");
  const format = await getFormatter();
  return (
    <main id="main" className="page content-signage">
      <ScreenRefresh seconds={feed.data.refreshSeconds} />
      <h1>
        <bdi>{feed.data.name}</bdi>
      </h1>
      {feed.data.items.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <ul className="content-signage-list" aria-label={t("items")}>
          {feed.data.items.map((i) => (
            <li key={i.id} lang={i.locale}>
              <TileCard title={i.title} icon="megaphone" headingLevel={2} meta={format.dateTime(new Date(i.publishedAt), { dateStyle: "medium" })}>
                {i.summary ? <p>{i.summary}</p> : null}
              </TileCard>
            </li>
          ))}
        </ul>
      )}
    </main>
  );
}
