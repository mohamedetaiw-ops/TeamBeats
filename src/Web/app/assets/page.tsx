import Link from "next/link";
import { redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { OrgUnitView } from "@/lib/admin/types";
import { nameIn } from "@/lib/documents/types";
import { assetRequestsHref, assetsApi, assetsHref, type Asset, type AssetCollection, type AssetPortal } from "@/lib/assets/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { AssetGrid, CollectionList, NewCollection, RequestAccess, UploadAsset } from "@/features/assets/AssetPortal";

/**
 * The brand asset portal (spec 011 R2 US5): collections on the side, the open collection's assets with their
 * renditions, a request for access to a restricted collection, and for brand managers new collections and uploads. The
 * API decides who reads and who curates; the page shows what it answers.
 */
export default async function AssetPortalPage({ searchParams }: { searchParams: Promise<{ collection?: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const { collection: wanted } = await searchParams;
  const t = await getTranslations("assets");
  const locale = await getLocale();
  const [collections, portal] = await Promise.all([apiGet<AssetCollection[]>(`${assetsApi}/collections`), apiGet<AssetPortal>(`${assetsApi}/portal`)]);
  const header = <Header me={me.data} breadcrumbs={[{ label: t("title"), href: assetsHref }]} />;
  if (!collections.ok) {
    return (
      <>
        {header}
        <main id="main" className="page">
          <PageHeader title={t("title")} description={t("intro")} icon="image" />
          <StateView kind="denied" title={t("unavailable")} message="" />
        </main>
      </>
    );
  }
  const current = collections.data.find((c) => c.id === wanted) ?? collections.data.find((c) => c.canRead) ?? collections.data[0] ?? null;
  const manage = portal.ok && portal.data.canManage;
  const [assets, units] = await Promise.all([
    current?.canRead ? apiGet<Asset[]>(`${assetsApi}/collections/${encodeURIComponent(current.id)}/assets`) : null,
    manage ? apiGet<OrgUnitView[]>("/api/v1/org/units") : null,
  ]);
  return (
    <>
      {header}
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="image" />
        {manage ? (
          <p>
            <Link href={assetRequestsHref}>{t("requestsLink")}</Link>
          </p>
        ) : null}
        <div className="assets-layout">
          <div className="assets-main">
            {collections.data.length === 0 ? <p className="assets-meta">{t("noCollections")}</p> : <CollectionList collections={collections.data} current={current?.id ?? null} />}
            {manage ? (
              <NewCollection
                units={units?.ok ? units.data.filter((u) => u.status === "active").map((u) => ({ id: u.id, name: nameIn(u.names, locale) })) : []}
              />
            ) : null}
          </div>
          <div className="assets-main">
            {current ? (
              <section aria-labelledby="asset-collection-title" className="assets-main">
                <h2 id="asset-collection-title">
                  <bdi>{nameIn(current.names, locale)}</bdi>
                </h2>
                {current.canRead && nameIn(current.descriptions, locale) ? (
                  <p>
                    <bdi>{nameIn(current.descriptions, locale)}</bdi>
                  </p>
                ) : null}
                {!current.canRead ? (
                  <RequestAccess collection={current} />
                ) : (
                  <>
                    {current.canManage && current.status === "active" ? <UploadAsset collection={current} /> : null}
                    <AssetGrid assets={assets?.ok ? assets.data : []} manage={current.canManage} />
                  </>
                )}
              </section>
            ) : null}
          </div>
        </div>
      </main>
    </>
  );
}
