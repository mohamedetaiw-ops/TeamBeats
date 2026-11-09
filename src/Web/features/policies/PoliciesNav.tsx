import Link from "next/link";
import { useTranslations } from "next-intl";
import "./policies.css";

/** Links between My policies and the register, with the current one marked. */
export function PoliciesNav({ current }: { current: "my" | "register" | null }) {
  const t = useTranslations("policies.nav");
  const links = [
    { key: "my", href: "/policies/my" },
    { key: "register", href: "/policies" },
  ] as const;
  return (
    <nav aria-label={t("label")} className="policies-nav">
      <ul>
        {links.map((l) => (
          <li key={l.key}>
            <Link href={l.href} aria-current={current === l.key ? "page" : undefined}>
              {t(l.key)}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
