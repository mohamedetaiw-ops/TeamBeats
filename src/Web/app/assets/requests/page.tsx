import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { assetRequestsHref, assetsApi, assetsHref, type AssetAccessRequest, type AssetDownloadReport } from "@/lib/assets/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { AccessRequests, DownloadReport } from "@/features/assets/AssetPortal";

/** Brand managers decide access requests and see who downloaded what (spec 011 R2 US5 scenarios 3 and 4). */
export default async function AssetRequestsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("assets");
  const [requests, downloads] = await Promise.all([
    apiGet<AssetAccessRequest[]>(`${assetsApi}/access-requests`),
    apiGet<AssetDownloadReport>(`${assetsApi}/downloads`),
  ]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: assetsHref },
          { label: t("requestsTitle"), href: assetRequestsHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("requestsTitle")} description={t("requestsIntro")} icon="image" />
        {!requests.ok || !downloads.ok ? (
          <StateView kind="denied" title={t("denied")} message="" />
        ) : (
          <>
            <AccessRequests requests={requests.data} />
            <DownloadReport report={downloads.data} />
          </>
        )}
      </main>
    </>
  );
}
