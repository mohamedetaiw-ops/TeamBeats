import { useTranslations } from "next-intl";
import { SectionNav } from "@/components/ui/SectionNav";
import "./policies.css";

/** Links between My policies, the register, for line managers their team, and for compliance officers the exemption register and evidence packs, with the current one marked. */
export function PoliciesNav({
  current,
  exemptions = false,
  evidence = false,
  team = false,
}: {
  current: "my" | "register" | "team" | "exemptions" | "evidence" | null;
  exemptions?: boolean;
  evidence?: boolean;
  team?: boolean;
}) {
  const t = useTranslations("policies.nav");
  const items = [
    { key: "my", href: "/policies/my", label: t("my") },
    { key: "register", href: "/policies", label: t("register") },
    ...(team ? [{ key: "team", href: "/policies/team", label: t("team") }] : []),
    ...(exemptions ? [{ key: "exemptions", href: "/policies/exemptions", label: t("exemptions") }] : []),
    ...(evidence ? [{ key: "evidence", href: "/policies/evidence", label: t("evidence") }] : []),
  ];
  return <SectionNav label={t("label")} items={items} current={current} />;
}
