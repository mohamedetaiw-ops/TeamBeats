import { useTranslations } from "next-intl";
import { SectionNav } from "@/components/ui/SectionNav";

/** Links between the people pages, with the current one marked. */
export function PeopleNav({ current }: { current: "directory" | "team" | "me" | null }) {
  const t = useTranslations("people.nav");
  const items = [
    { key: "directory", href: "/people", label: t("directory") },
    { key: "team", href: "/people/team", label: t("team") },
    { key: "me", href: "/people/me", label: t("me") },
  ];
  return <SectionNav label={t("label")} items={items} current={current} />;
}
