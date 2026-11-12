import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { learningHref, type CertificateView } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { VerifyForm } from "@/features/learning/Certificate";

/** Checks a certificate ID (spec 028 FR-010). Only people the API lets see the certificate get its details. */
export default async function VerifyPage({ searchParams }: { searchParams: Promise<{ code?: string | string[] }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const given = (await searchParams).code;
  const code = ((Array.isArray(given) ? given[0] : given) ?? "").trim().slice(0, 20);
  const t = await getTranslations("learning");
  let found: CertificateView | null | "denied" = null;
  if (code) {
    const result = await apiGet<CertificateView>(`/api/v1/learning/certificates/verify?code=${encodeURIComponent(code)}`);
    found = result.ok ? result.data : result.status === 403 ? "denied" : null;
  }
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: learningHref },
          { label: t("verify.pageTitle"), href: "/learning/certificates/verify" },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("verify.pageTitle")} description={t("verify.pageIntro")} icon="check" />
        <VerifyForm code={code} found={found} />
      </main>
    </>
  );
}
