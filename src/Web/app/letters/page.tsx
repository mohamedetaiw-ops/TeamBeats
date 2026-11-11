import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { LetterChoice } from "@/lib/letters";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { MyLetters } from "@/features/letters/MyLetters";

/** My HR letters (spec 022 US7): ask for a letter and download the issued ones. The API decides which types apply. */
export default async function LettersPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("letters");
  const choices = await apiGet<LetterChoice[]>("/api/v1/me/letters/types");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/letters" }]} />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="files" />
        {choices.ok ? <MyLetters choices={choices.data} /> : <StateView kind="empty" title={t("unavailableTitle")} message={t("unavailable")} />}
      </main>
    </>
  );
}
