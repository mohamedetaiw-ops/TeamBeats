import Link from "next/link";
import { getTranslations } from "next-intl/server";
import type { NavigationSection } from "@/lib/api";
import { Icon } from "@/components/ui/Icon";

export async function AdminNav({ label, sections }: { label: string; sections: NavigationSection[] }) {
  const t = await getTranslations();
  return (
    <nav aria-label={label} className="admin-nav">
      <ul>
        {sections.map((s) => (
          <li key={s.key}>
            <Link href={s.path}>
              <span>{t(s.labelKey)}</span>
              <Icon name="chevron-forward" mirrorInRtl />
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
