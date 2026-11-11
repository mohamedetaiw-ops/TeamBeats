import type { Metadata } from "next";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { Card } from "@/components/ui/Card";
import { StateView } from "@/components/ui/StateView";
import { apiGet } from "@/lib/api";
import type { PreboardingPage } from "@/lib/journeys";
import { pick } from "@/lib/services/types";
import "@/features/journeys/journeys.css";

export const dynamic = "force-dynamic";

// The link is personal: never sent on as a referrer, never indexed.
export const metadata: Metadata = { referrer: "no-referrer", robots: { index: false, follow: false } };

/**
 * Pre-boarding (spec 022 US4 scenario 3, FR-016): what a joiner without an account reads from the time-limited link
 * sent to their personal email. It shows the start date and the pre-boarding text of the template, nothing else.
 * The API resolves the tenant from the host and the token; an unknown or ended link reads as not available.
 */
export default async function PreboardingPageView({ params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;
  const t = await getTranslations("preboarding");
  const locale = await getLocale();
  const format = await getFormatter();
  const page = /^[A-Za-z0-9_-]{43}$/.test(token) ? await apiGet<PreboardingPage>(`/api/v1/preboarding/${encodeURIComponent(token)}`) : null;
  if (!page?.ok) {
    return (
      <main id="main" className="page narrow">
        <StateView kind="empty" title={t("unavailableTitle")} message={t("unavailable")} />
      </main>
    );
  }
  const date = (iso: string) => format.dateTime(new Date(`${iso}T00:00:00Z`), { dateStyle: "long", timeZone: "UTC" });
  return (
    <main id="main" className="page narrow">
      <h1>{t("title")}</h1>
      <p className="notice">{t("startsOn", { date: date(page.data.startDate) })}</p>
      <ul className="preboarding-steps">
        {page.data.steps.map((s) => (
          <li key={s.key}>
            <Card title={pick(s.names, locale, s.key)} headingLevel={2} eyebrow={date(s.showsOn)}>
              <p className="journeys-content">{pick(s.content, locale, "")}</p>
            </Card>
          </li>
        ))}
      </ul>
      <p className="field-hint">{t("expires", { at: format.dateTime(new Date(page.data.expiresAt), { dateStyle: "long", timeStyle: "short" }) })}</p>
    </main>
  );
}
