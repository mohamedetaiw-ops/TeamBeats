import { getTranslations } from "next-intl/server";
import { SectionNav } from "@/components/ui/SectionNav";
import { apiGet } from "@/lib/api";
import type { SkillListView } from "@/lib/people/skills";

/**
 * Links between the people pages, with the current one marked. Find an expert is listed only while the API says the
 * tenant has skills on (spec 010 R2 US6).
 */
export async function PeopleNav({ current }: { current: "directory" | "team" | "me" | "experts" | null }) {
  const t = await getTranslations("people.nav");
  const skills = await apiGet<SkillListView>("/api/v1/people/skills");
  const items = [
    { key: "directory", href: "/people", label: t("directory") },
    ...(skills.ok && skills.data.available && skills.data.enabled ? [{ key: "experts", href: "/people/experts", label: t("experts") }] : []),
    { key: "team", href: "/people/team", label: t("team") },
    { key: "me", href: "/people/me", label: t("me") },
  ];
  return <SectionNav label={t("label")} items={items} current={current} />;
}
