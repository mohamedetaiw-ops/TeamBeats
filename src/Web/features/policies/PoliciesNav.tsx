import { useTranslations } from "next-intl";
import { SectionNav } from "@/components/ui/SectionNav";
import "./policies.css";

/** Links between My policies and the register, with the current one marked. */
export function PoliciesNav({ current }: { current: "my" | "register" | null }) {
  const t = useTranslations("policies.nav");
  const items = [
    { key: "my", href: "/policies/my", label: t("my") },
    { key: "register", href: "/policies", label: t("register") },
  ];
  return <SectionNav label={t("label")} items={items} current={current} />;
}
