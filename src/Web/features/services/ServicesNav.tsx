import { useTranslations } from "next-intl";
import { SectionNav } from "@/components/ui/SectionNav";
import "./services.css";

/** Links between the services pages, with the current one marked. */
export function ServicesNav({ current }: { current: "catalogue" | "requests" | "team" | "tasks" | null }) {
  const t = useTranslations("services.nav");
  const items = [
    { key: "catalogue", href: "/services", label: t("catalogue") },
    { key: "requests", href: "/requests", label: t("requests") },
    { key: "team", href: "/requests/team", label: t("team") },
    { key: "tasks", href: "/tasks", label: t("tasks") },
  ];
  return <SectionNav label={t("label")} items={items} current={current} />;
}
