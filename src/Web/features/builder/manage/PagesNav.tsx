"use client";

import { useTranslations } from "next-intl";
import { SectionNav } from "@/components/ui/SectionNav";

const items = [
  { key: "pages", href: "/admin/pages" },
  { key: "routes", href: "/admin/pages/routes" },
  { key: "fragments", href: "/admin/pages/fragments" },
  { key: "controls", href: "/admin/pages/controls" },
  { key: "upgrades", href: "/admin/pages/upgrades" },
] as const;

/** The pages of the Pages area (spec 032 R2 T032). Each screen asks the API again, so a tab never grants anything. */
export function PagesNav({ current }: { current: (typeof items)[number]["key"] }) {
  const t = useTranslations("builder.manage.nav");
  return <SectionNav label={t("label")} current={current} items={items.map((i) => ({ ...i, label: t(i.key) }))} />;
}
