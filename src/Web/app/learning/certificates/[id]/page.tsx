import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { certificateHref, isGuid, learningHref, type CertificateView } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { Certificate } from "@/features/learning/Certificate";

/** A certificate of completion (spec 028 FR-010), for its holder or a learning reporter in scope. */
export default async function CertificatePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const certificate = await apiGet<CertificateView>(`/api/v1/learning/certificates/${id}`);
  if (!certificate.ok) {
    notFound();
  }
  const t = await getTranslations("learning");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: learningHref },
          { label: t("certificate.pageTitle"), href: certificateHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("certificate.pageTitle")} icon="star" />
        <Certificate certificate={certificate.data} />
      </main>
    </>
  );
}
