import Link from "next/link";
import { getFormatter, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { getShell } from "@/lib/shell/client";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";

/**
 * The date the conformance map (docs/accessibility/wcag-2.2-conformance.md) was last reviewed. Change it in the same
 * pull request as the map, so the statement never claims a newer check than the one recorded.
 */
const lastReviewed = new Date("2026-11-18T00:00:00Z");

const limitations = ["drag", "captions", "pdf", "audit", "timeout"] as const;
const settings = ["text", "motion", "mode", "density"] as const;

/**
 * The TeamBeats accessibility statement (spec 038 US6, A11Y-023). Public: it renders signed in, with the shell, and
 * signed out. When the tenant links its own accessibility page in the footer, the statement shows it as the
 * organization's contact; otherwise it tells the person to contact their TeamBeats administrator.
 */
export default async function AccessibilityStatementPage() {
  const [me, shell, t, format] = await Promise.all([apiGet<Me>("/api/v1/me"), getShell(), getTranslations("accessibility_statement"), getFormatter()]);
  const own = shell.ok ? shell.data.footer?.links.find((link) => link.kind === "accessibility") : undefined;
  return (
    <>
      {me.ok ? <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/accessibility" }]} /> : null}
      <main id="main" className="page narrow">
        <PageHeader title={t("title")} description={t("intro")} icon="info" />

        <section aria-labelledby="statement-standard">
          <h2 id="statement-standard">{t("standard.title")}</h2>
          <p>{t("standard.body")}</p>
        </section>

        <section aria-labelledby="statement-checks">
          <h2 id="statement-checks">{t("checks.title")}</h2>
          <ul>
            <li>{t("checks.automated")}</li>
            <li>{t("checks.pages")}</li>
            <li>{t("checks.manual")}</li>
          </ul>
          <p>{t("checks.reviewed", { date: format.dateTime(lastReviewed, { dateStyle: "long", timeZone: "UTC" }) })}</p>
        </section>

        <section aria-labelledby="statement-limitations">
          <h2 id="statement-limitations">{t("limitations.title")}</h2>
          <ul>
            {limitations.map((key) => (
              <li key={key}>{t(`limitations.${key}`)}</li>
            ))}
          </ul>
          <p>{t("limitations.content")}</p>
        </section>

        <section aria-labelledby="statement-settings">
          <h2 id="statement-settings">{t("settings.title")}</h2>
          <ul>
            {settings.map((key) => (
              <li key={key}>{t(`settings.${key}`)}</li>
            ))}
          </ul>
          {me.ok ? (
            <p>
              <Link href="/settings">{t("settings.open")}</Link>
            </p>
          ) : null}
        </section>

        <section aria-labelledby="statement-report">
          <h2 id="statement-report">{t("report.title")}</h2>
          {own ? (
            <p>
              {t("report.own")}{" "}
              <a href={own.href} rel={own.href.startsWith("/") ? undefined : "noopener noreferrer"}>
                {own.label}
              </a>
            </p>
          ) : (
            <p>{t("report.admin")}</p>
          )}
        </section>
      </main>
    </>
  );
}
