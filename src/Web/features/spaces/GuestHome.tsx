import Link from "next/link";
import { getFormatter, getTranslations } from "next-intl/server";
import { Badge } from "@/components/ui/Badge";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { spaceHref, type GuestSpace } from "@/lib/spaces";
import "./spaces.css";

/**
 * The guest home (spec 026 section 10): the spaces a guest was invited to, each with the person who invited them and
 * the day their access ends. The API lists only the caller's own guest memberships.
 */
export async function GuestHome({ spaces, ok }: { spaces: GuestSpace[]; ok: boolean }) {
  const [t, format] = await Promise.all([getTranslations("spaces.guestHome"), getFormatter()]);
  return (
    <main id="main" className="page">
      <PageHeader title={t("title")} description={t("intro")} icon="folder" />
      {!ok ? (
        <StateView kind="error" title={t("failedTitle")} message={t("failed")} />
      ) : spaces.length === 0 ? (
        <StateView kind="empty" title={t("noneTitle")} message={t("none")} icon="folder" />
      ) : (
        <ul className="spaces-grid" aria-label={t("listLabel")}>
          {spaces.map((space) => (
            <li key={space.spaceId} className="spaces-card">
              <h2>
                <Link href={spaceHref(space.spaceId)}>
                  <bdi>{space.name}</bdi>
                </Link>
              </h2>
              <div className="spaces-row">
                <Badge tone="info">{t("guest")}</Badge>
                <Badge tone="neutral">{t(`roles.${space.role === "member" ? "member" : "viewer"}`)}</Badge>
              </div>
              {space.sponsorName ? (
                <p className="spaces-meta">
                  {t("sponsor")} <bdi>{space.sponsorName}</bdi>
                </p>
              ) : null}
              <p className="spaces-meta">
                {space.expiresAt ? t("until", { date: format.dateTime(new Date(space.expiresAt), { dateStyle: "long" }) }) : t("noEnd")}
              </p>
            </li>
          ))}
        </ul>
      )}
    </main>
  );
}
