import { useTranslations } from "next-intl";
import { SectionNav } from "@/components/ui/SectionNav";
import "./policies.css";

/** Links between My policies, the register and, for compliance officers, the exemption register and evidence packs, with the current one marked. */
export function PoliciesNav({
  current,
  exemptions = false,
  evidence = false,
}: {
  current: "my" | "register" | "exemptions" | "evidence" | null;
  exemptions?: boolean;
  evidence?: boolean;
}) {
  const t = useTranslations("policies.nav");
  const items = [
    { key: "my", href: "/policies/my", label: t("my") },
    { key: "register", href: "/policies", label: t("register") },
    ...(exemptions ? [{ key: "exemptions", href: "/policies/exemptions", label: t("exemptions") }] : []),
    ...(evidence ? [{ key: "evidence", href: "/policies/evidence", label: t("evidence") }] : []),
  ];
  return <SectionNav label={t("label")} items={items} current={current} />;
}
