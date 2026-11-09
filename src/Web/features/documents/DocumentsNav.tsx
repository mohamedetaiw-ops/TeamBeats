import Link from "next/link";
import { useTranslations } from "next-intl";
import "./documents.css";

/** Links between the document libraries and the knowledge base, with the current one marked. */
export function DocumentsNav({
  current,
}: {
  current: "libraries" | "kb" | null;
}) {
  const t = useTranslations("documents.nav");
  const links = [
    { key: "libraries", href: "/documents" },
    { key: "kb", href: "/kb" },
  ] as const;
  return (
    <nav aria-label={t("label")} className="documents-nav">
      <ul>
        {links.map((l) => (
          <li key={l.key}>
            <Link
              href={l.href}
              aria-current={current === l.key ? "page" : undefined}
            >
              {t(l.key)}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}
