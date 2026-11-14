import Link from "next/link";
import { redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { pickName, type FollowView, type SiteLink } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { FollowButton } from "@/features/content/FollowButton";
import "@/features/content/content.css";

/**
 * Sites for the reader (spec 009 US6 scenario 2): the sites of their part of the organization and the sites shared
 * with them, each opening its home page. Archived sites are not listed.
 */
export default async function SitesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [sites, follows] = await Promise.all([apiGet<SiteLink[]>("/api/v1/content/sites/mine"), apiGet<FollowView[]>("/api/v1/content/follows")]);
  const followed = new Set(follows.ok ? follows.data.filter((f) => f.kind === "site").map((f) => f.key) : []);
  const t = await getTranslations("content.sites");
  const locale = await getLocale();
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("mine"), href: "/sites" }]} />
      <main id="main" className="page content-manage">
        <h1>{t("mine")}</h1>
        <p className="field-hint">{t("mineIntro")}</p>
        {!sites.ok || sites.data.length === 0 ? (
          <StateView kind="empty" title={t("mineEmpty")} message="" />
        ) : (
          <ul className="content-site-list">
            {sites.data.map((s) => {
              const name = pickName(s.names, locale);
              return (
                <li key={s.id} className="content-card" data-testid="site-link">
                  <h2>
                    <bdi>{name}</bdi>
                  </h2>
                  {s.homeSlug ? (
                    <Link href={`/p/${s.homeSlug}`} aria-label={t("open", { name })}>
                      {t("home")}
                    </Link>
                  ) : (
                    <p className="content-meta">{t("notPublished")}</p>
                  )}
                  <FollowButton kind="site" followKey={s.id} label={name} following={followed.has(s.id)} />
                </li>
              );
            })}
          </ul>
        )}
      </main>
    </>
  );
}
