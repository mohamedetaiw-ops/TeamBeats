import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { OwnProfile } from "@/lib/people/types";
import { Header } from "@/components/shell/Header";
import { OwnProfileForm } from "@/features/people/OwnProfileForm";
import { PeopleNav } from "@/features/people/PeopleNav";
import { PageHeader } from "@/components/ui/PageHeader";

/** The signed-in person's own profile settings (spec 010 US1). */
export default async function OwnProfilePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const own = await apiGet<OwnProfile>("/api/v1/me/profile");
  if (!own.ok) {
    notFound();
  }
  const t = await getTranslations("people");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/people" },
          { label: t("me.title"), href: "/people/me" },
        ]}
      />
      <main id="main" className="page people">
        <PageHeader title={t("me.title")} description={t("me.intro")} icon="people" nav={<PeopleNav current="me" />} />
        <OwnProfileForm initial={own.data} />
      </main>
    </>
  );
}
