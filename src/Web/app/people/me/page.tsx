import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { OwnProfile } from "@/lib/people/types";
import type { OwnSkillsView, SkillSuggestionsView } from "@/lib/people/skills";
import { OwnSkillsEditor } from "@/features/people/Skills";
import { SkillSuggestions } from "@/features/people/SkillSuggestions";
import { CompletenessPrompt } from "@/features/people/Completeness";
import { localName } from "@/lib/people/types";
import { getLocale } from "next-intl/server";
import { Header } from "@/components/shell/Header";
import { OwnProfileForm } from "@/features/people/OwnProfileForm";
import { PeopleNav } from "@/features/people/PeopleNav";
import { PageHeader } from "@/components/ui/PageHeader";

/** The signed-in person's own profile settings (spec 010 US1) and skills (R2 US6). */
export default async function OwnProfilePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [own, skills, suggestions] = await Promise.all([
    apiGet<OwnProfile>("/api/v1/me/profile"),
    apiGet<OwnSkillsView>("/api/v1/me/skills"),
    apiGet<SkillSuggestionsView>("/api/v1/me/skills/suggestions"),
  ]);
  if (!own.ok) {
    notFound();
  }
  const t = await getTranslations("people");
  const locale = await getLocale();
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
        {own.data.completeness ? (
          <CompletenessPrompt
            initial={own.data.completeness}
            fieldLabel={Object.fromEntries(own.data.fields.map((f) => [f.key, f.names ? localName(f.names, locale, f.key) : t(`field.${f.key}` as "field.email")]))}
          />
        ) : null}
        <OwnProfileForm initial={own.data} />
        {skills.ok ? <OwnSkillsEditor initial={skills.data} /> : null}
        {suggestions.ok ? <SkillSuggestions initial={suggestions.data} /> : null}
      </main>
    </>
  );
}
