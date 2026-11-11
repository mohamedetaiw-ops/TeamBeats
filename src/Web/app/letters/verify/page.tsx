import type { Metadata } from "next";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { apiGet } from "@/lib/api";
import type { LetterVerification } from "@/lib/letters";
import { pick } from "@/lib/services/types";
import "@/features/letters/letters.css";

export const dynamic = "force-dynamic";

// Whoever holds a letter checks it here without signing in; the page is never indexed.
export const metadata: Metadata = { referrer: "no-referrer", robots: { index: false, follow: false } };

/**
 * Letter verification (spec 022 US7 scenario 2, FR-022): typing the code printed on a letter shows its reference,
 * type, issue date and the company, nothing else. The API resolves the tenant from the host; an unknown code, a
 * turned off page or a letter of another organization all read the same.
 */
export default async function LetterVerifyPage({ searchParams }: { searchParams: Promise<{ code?: string }> }) {
  const { code } = await searchParams;
  const t = await getTranslations("letters.verify");
  const locale = await getLocale();
  const format = await getFormatter();
  const clean = typeof code === "string" ? code.trim().slice(0, 20) : "";
  const result = /^[A-Za-z0-9 -]{12,20}$/.test(clean) ? await apiGet<LetterVerification>(`/api/v1/letters/verify/${encodeURIComponent(clean)}`) : null;
  return (
    <main id="main" className="page narrow">
      <div className="letters-verify">
        <h1>{t("title")}</h1>
        <p className="notice">{t("intro")}</p>
        <form method="get" action="/letters/verify" className="letters-row">
          <FormField label={t("code")} name="code" defaultValue={clean} maxLength={20} autoComplete="off" dir="ltr" />
          <Button type="submit" variant="primary">
            {t("check")}
          </Button>
        </form>
        {clean && result?.ok ? (
          <Alert tone="success" title={t("valid")} live>
            <dl className="letters-facts">
              <div>
                <dt>{t("reference")}</dt>
                <dd>
                  <bdi dir="ltr">{result.data.reference}</bdi>
                </dd>
              </div>
              <div>
                <dt>{t("type")}</dt>
                <dd>{pick(result.data.typeNames, locale, "")}</dd>
              </div>
              <div>
                <dt>{t("issuedOn")}</dt>
                <dd>{format.dateTime(new Date(`${result.data.issuedOn}T00:00:00Z`), { dateStyle: "long", timeZone: "UTC" })}</dd>
              </div>
              <div>
                <dt>{t("company")}</dt>
                <dd>
                  <bdi>{result.data.companyName}</bdi>
                </dd>
              </div>
            </dl>
          </Alert>
        ) : clean ? (
          <Alert tone="warning" title={t("notFoundTitle")} live>
            {t("notFound")}
          </Alert>
        ) : null}
      </div>
    </main>
  );
}
