import Link from "next/link";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { pickName, sharedSiteHref, type SharedSite } from "@/lib/content/types";
import { Widget } from "@/components/ui/Widget";
import "./spaces.css";

/**
 * Intranet sites shared with this space (spec 009 R3 US10), on the space page for its readers: guests open each one
 * read-only from here. Nothing shows when none is shared or the plan has no guest sites.
 */
export async function SharedSites({ spaceId, sites }: { spaceId: string; sites: SharedSite[] }) {
  if (sites.length === 0) return null;
  const [t, locale, format] = await Promise.all([getTranslations("spaces.sharedSites"), getLocale(), getFormatter()]);
  return (
    <Widget title={t("title")}>
      <p className="field-hint">{t("intro")}</p>
      <ul className="spaces-shared-sites" data-testid="shared-sites">
        {sites.map((s) => (
          <li key={s.shareId} className="spaces-shared-site">
            <Link href={sharedSiteHref(spaceId, s.siteId)}>
              <bdi>{pickName(s.names, locale)}</bdi>
            </Link>
            <span className="field-hint"> {t("until", { date: format.dateTime(new Date(s.expiresAt), { dateStyle: "medium" }) })}</span>
          </li>
        ))}
      </ul>
    </Widget>
  );
}
