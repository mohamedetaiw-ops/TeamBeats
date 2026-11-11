import { useTranslations } from "next-intl";
import { Menu } from "./Menu";

export type Crumb = { href: string; label: string };

/**
 * Breadcrumb (spec 036 DLS-038): where the page sits, from the top of the site to the page itself. A long trail keeps
 * its first level and its last two; the levels between collapse into a menu so the trail stays on one line on a phone.
 */
export function Breadcrumb({ label, crumbs, className, max = 4 }: { label: string; crumbs: readonly Crumb[]; className?: string; max?: number }) {
  const t = useTranslations("ui.controls");
  const collapse = crumbs.length > max;
  const head = collapse ? crumbs.slice(0, 1) : crumbs;
  const hidden = collapse ? crumbs.slice(1, -2) : [];
  const tail = collapse ? crumbs.slice(-2) : [];
  const last = crumbs[crumbs.length - 1];
  const link = (crumb: Crumb) => (
    <li key={crumb.href}>
      <a href={crumb.href} aria-current={crumb === last ? "page" : undefined}>
        <bdi>{crumb.label}</bdi>
      </a>
    </li>
  );
  return (
    <nav aria-label={label} className={["ui-breadcrumb", className].filter(Boolean).join(" ")}>
      <ol>
        {head.map(link)}
        {collapse ? (
          <li>
            <Menu label={t("moreLevels")} iconOnly variant="quiet" size="sm" items={hidden.map((crumb) => ({ key: crumb.href, label: crumb.label, href: crumb.href }))} />
          </li>
        ) : null}
        {tail.map(link)}
      </ol>
    </nav>
  );
}
