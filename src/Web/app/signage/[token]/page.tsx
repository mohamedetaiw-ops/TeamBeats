import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickName, type SignageFeed } from "@/lib/content/types";
import "@/features/content/content.css";

export const dynamic = "force-dynamic";

// A screen in a lobby opens this address without signing in; the page is never indexed and sends no referrer.
export const metadata: Metadata = { referrer: "no-referrer", robots: { index: false, follow: false } };

/**
 * A digital signage screen (spec 009 section 4): the live Public items of one site meant for everyone, read only, from
 * the address its site owner made. The API resolves the tenant from the host; an unknown or replaced address reads as
 * not found.
 */
export default async function SignagePage({ params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;
  if (!/^[A-Za-z0-9_-]{20,100}$/.test(token)) {
    notFound();
  }
  const feed = await apiGet<SignageFeed>(`/api/v1/signage/${encodeURIComponent(token)}`);
  if (!feed.ok) {
    notFound();
  }
  const t = await getTranslations("content.signage");
  const locale = await getLocale();
  const format = await getFormatter();
  return (
    <main id="main" className="page content-signage">
      <h1>
        <bdi>{pickName(feed.data.siteNames, locale)}</bdi>
      </h1>
      {feed.data.items.length === 0 ? (
        <p>{t("empty")}</p>
      ) : (
        <ul className="content-signage-list" aria-label={t("items")}>
          {feed.data.items.map((i) => (
            <li key={i.id} lang={i.locale} className="content-card">
              <h2 className="content-signage-title">
                <bdi>{i.title}</bdi>
              </h2>
              {i.summary ? <p>{i.summary}</p> : null}
              <p className="content-meta">{format.dateTime(new Date(i.publishedAt), { dateStyle: "medium" })}</p>
            </li>
          ))}
        </ul>
      )}
    </main>
  );
}
