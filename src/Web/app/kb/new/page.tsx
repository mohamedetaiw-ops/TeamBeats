import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { KbTemplate } from "@/lib/documents/types";
import { Header } from "@/components/shell/Header";
import { DocumentsNav } from "@/features/documents/DocumentsNav";
import { NewArticleForm } from "@/features/documents/NewArticleForm";

/** Starts a knowledge article from a template (spec 011 US4 scenario 1); writing it continues in the content editor. */
export default async function NewArticlePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const templates = await apiGet<KbTemplate[]>("/api/v1/kb/templates");
  if (!templates.ok) {
    notFound();
  }
  const t = await getTranslations("documents.kb");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/kb" },
          { label: t("new"), href: "/kb/new" },
        ]}
      />
      <main id="main" className="page">
        <h1>{t("new")}</h1>
        <DocumentsNav current="kb" />
        <NewArticleForm
          templates={templates.data}
          locales={me.data.tenant.enabledLocales}
          defaultLocale={me.data.tenant.defaultLocale}
        />
      </main>
    </>
  );
}
