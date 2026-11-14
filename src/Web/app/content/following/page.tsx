import Link from "next/link";
import { redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { pickName, topicHref, type FeedPage, type FollowView, type SiteLink } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { FollowButton } from "@/features/content/FollowButton";
import "@/features/content/content.css";

/**
 * The following feed (spec 009 FR-017): items of the sites and topics the person follows, with critical items always
 * included, and the list of what they follow with a way to stop.
 */
export default async function FollowingPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [feed, follows, sites] = await Promise.all([
    apiGet<FeedPage>("/api/v1/content/feed?following=true&limit=30"),
    apiGet<FollowView[]>("/api/v1/content/follows"),
    apiGet<SiteLink[]>("/api/v1/content/sites/mine"),
  ]);
  const t = await getTranslations("content.topics");
  const locale = await getLocale();
  const date = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(iso));
  const siteName = (id: string) => {
    const site = sites.ok ? sites.data.find((s) => s.id === id) : undefined;
    return site ? pickName(site.names, locale) : t("site");
  };
  const list = follows.ok ? follows.data : [];
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("followingTitle"), href: "/content/following" }]} />
      <main id="main" className="page content-manage">
        <h1>{t("followingTitle")}</h1>
        <p className="field-hint">{t("followingIntro")}</p>
        <section aria-labelledby="follows">
          <h2 id="follows">{t("follows")}</h2>
          {list.length === 0 ? (
            <p className="content-meta">{t("followsEmpty")}</p>
          ) : (
            <ul className="content-follow-list">
              {list.map((f) => {
                const label = f.kind === "topic" ? `#${f.key}` : siteName(f.key);
                return (
                  <li key={`${f.kind}:${f.key}`}>
                    {f.kind === "topic" ? (
                      <Link href={topicHref(f.key)}>
                        <bdi>{label}</bdi>
                      </Link>
                    ) : (
                      <bdi>{label}</bdi>
                    )}
                    <FollowButton kind={f.kind} followKey={f.key} label={label} following />
                  </li>
                );
              })}
            </ul>
          )}
        </section>
        <section aria-labelledby="following-feed">
          <h2 id="following-feed">{t("feed")}</h2>
          {!feed.ok || feed.data.items.length === 0 ? (
            <StateView kind="empty" title={t("feedEmpty")} message="" />
          ) : (
            <ul className="content-site-list">
              {feed.data.items.map((i) => (
                <li key={i.id} className="content-card" data-testid="following-item">
                  <h3>
                    <Link href={`/content/read/${encodeURIComponent(i.id)}`}>
                      <bdi>{i.title}</bdi>
                    </Link>
                  </h3>
                  <p className="content-meta">{date(i.publishedAt)}</p>
                </li>
              ))}
            </ul>
          )}
        </section>
      </main>
    </>
  );
}
