import { useTranslations } from "next-intl";
import { SectionNav } from "@/components/ui/SectionNav";
import "./documents.css";

/** Links between the document libraries and the knowledge base, with the current one marked. */
export function DocumentsNav({ current }: { current: "libraries" | "kb" | null }) {
  const t = useTranslations("documents.nav");
  const items = [
    { key: "libraries", href: "/documents", label: t("libraries") },
    { key: "kb", href: "/kb", label: t("kb") },
  ];
  return <SectionNav label={t("label")} items={items} current={current} />;
}
