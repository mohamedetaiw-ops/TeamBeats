import Link from "next/link";
import { useTranslations } from "next-intl";
import { Icon } from "./Icon";

/** The pages to show: the first, the last, the current one with a neighbor on each side, and gaps between. */
export function pageWindow(page: number, pageCount: number): (number | "gap")[] {
  const wanted = new Set([1, pageCount, page - 1, page, page + 1].filter((p) => p >= 1 && p <= pageCount));
  const sorted = [...wanted].sort((a, b) => a - b);
  const out: (number | "gap")[] = [];
  sorted.forEach((p, i) => {
    const previous = sorted[i - 1];
    if (previous !== undefined && p - previous === 2) out.push(previous + 1);
    else if (previous !== undefined && p - previous > 2) out.push("gap");
    out.push(p);
  });
  return out;
}

/**
 * Pagination (spec 036 DLS-038) for lists that the API pages. Every page is a link, so pages can be opened in a new
 * tab, bookmarked and reached without script; the current page is marked. One page shows nothing.
 */
export function Pagination({ label, page, pageCount, hrefFor }: { label?: string; page: number; pageCount: number; hrefFor: (page: number) => string }) {
  const t = useTranslations("ui.controls");
  if (pageCount <= 1) return null;
  return (
    <nav className="ui-pagination" aria-label={label ?? t("pagination")}>
      <ul>
        <li>
          {page > 1 ? (
            <Link href={hrefFor(page - 1)} rel="prev" className="ui-page ui-page-step">
              <Icon name="chevron-forward" size={16} />
              <span>{t("previous")}</span>
            </Link>
          ) : (
            <span className="ui-page ui-page-step" aria-disabled="true">
              <Icon name="chevron-forward" size={16} />
              <span>{t("previous")}</span>
            </span>
          )}
        </li>
        {pageWindow(page, pageCount).map((p, i) =>
          p === "gap" ? (
            <li key={`gap-${i}`} aria-hidden="true">
              <span className="ui-page ui-page-gap">
                <Icon name="more" size={16} />
              </span>
            </li>
          ) : (
            <li key={p}>
              <Link href={hrefFor(p)} className="ui-page" aria-label={t("page", { page: p })} aria-current={p === page ? "page" : undefined}>
                {p}
              </Link>
            </li>
          ),
        )}
        <li>
          {page < pageCount ? (
            <Link href={hrefFor(page + 1)} rel="next" className="ui-page ui-page-step">
              <span>{t("next")}</span>
              <Icon name="chevron-forward" size={16} />
            </Link>
          ) : (
            <span className="ui-page ui-page-step" aria-disabled="true">
              <span>{t("next")}</span>
              <Icon name="chevron-forward" size={16} />
            </span>
          )}
        </li>
      </ul>
    </nav>
  );
}
