import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { complianceApi, complianceHref, libraryHref, type LibraryView } from "@/lib/compliance";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { FrameworkAdmin } from "@/features/compliance/FrameworkAdmin";

/** The control library (spec 026 FR-021), for people who read or manage compliance projects. */
export default async function ComplianceLibraryPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const library = await apiGet<LibraryView>(`${complianceApi}/library`);
  if (!library.ok) {
    notFound();
  }
  const t = await getTranslations("compliance");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: complianceHref },
          { label: t("library.title"), href: libraryHref },
        ]}
      />
      <main id="main" className="page compliance">
        <PageHeader title={t("library.title")} description={t("library.intro")} icon="book" />
        <FrameworkAdmin library={library.data} />
      </main>
    </>
  );
}
