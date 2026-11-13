import { useTranslations } from "next-intl";
import { SectionNav } from "@/components/ui/SectionNav";
import "./services.css";

/** Links between the services pages, with the current one marked. Tasks is its own area, not a tab here (spec 016 FR-017). */
export function ServicesNav({ current }: { current: "catalogue" | "requests" | "team" | null }) {
  const t = useTranslations("services.nav");
  const items = [
    { key: "catalogue", href: "/services", label: t("catalogue") },
    { key: "requests", href: "/requests", label: t("requests") },
    { key: "team", href: "/requests/team", label: t("team") },
  ];
  return <SectionNav label={t("label")} items={items} current={current} />;
}
