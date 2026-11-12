import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { awardsHref, type AwardList } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Awards } from "@/features/engagement/Awards";

/** Team and star of the period (spec 023 US5). The API decides who may open a period and who chooses the winners. */
export default async function AwardsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("engagement");
  const list = await apiGet<AwardList>("/api/v1/engagement/awards");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: "/communities" },
          { label: t("awards.pageTitle"), href: awardsHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("awards.pageTitle")} description={t("awards.pageIntro")} icon="spark" />
        {list.ok ? (
          <Awards initial={list.data} />
        ) : (
          <StateView kind={list.status === 403 ? "unsupported" : "error"} title={t("awards.unavailableTitle")} message={t("awards.unavailable")} />
        )}
      </main>
    </>
  );
}
