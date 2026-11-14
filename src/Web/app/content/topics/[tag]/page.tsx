import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { FeedPage, FollowView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { FollowButton } from "@/features/content/FollowButton";
import "@/features/content/content.css";

/**
 * A topic page (spec 009 US9 scenario 3): the live items with the hashtag that the reader may read and that target
 * them, newest first, with a button to follow the topic. Counts and lists never include items the reader may not see.
 */
export default async function TopicPage({ params }: { params: Promise<{ tag: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const { tag: raw } = await params;
  const tag = decodeURIComponent(raw).toLowerCase();
  const [page, follows] = await Promise.all([
    apiGet<FeedPage>(`/api/v1/content/topics/${encodeURIComponent(tag)}?limit=50`),
    apiGet<FollowView[]>("/api/v1/content/follows"),
  ]);
  if (!page.ok) {
    notFound();
  }
  const t = await getTranslations("content.topics");
  const locale = await getLocale();
  const date = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(iso));
  const name = `#${tag}`;
  const following = follows.ok && follows.data.some((f) => f.kind === "topic" && f.key === tag);
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: name, href: `/content/topics/${encodeURIComponent(tag)}` }]} />
      <main id="main" className="page content-manage">
        <h1>
          <bdi>{name}</bdi>
        </h1>
        <p className="content-actions">
          <FollowButton kind="topic" followKey={tag} label={name} following={following} />
        </p>
        {page.data.items.length === 0 ? (
          <StateView kind="empty" title={t("empty")} message="" />
        ) : (
          <ul className="content-site-list">
            {page.data.items.map((i) => (
              <li key={i.id} className="content-card" data-testid="topic-item">
                <h2>
                  <Link href={`/content/read/${encodeURIComponent(i.id)}`}>
                    <bdi>{i.title}</bdi>
                  </Link>
                </h2>
                <p className="content-meta">{date(i.publishedAt)}</p>
              </li>
            ))}
          </ul>
        )}
      </main>
    </>
  );
}
