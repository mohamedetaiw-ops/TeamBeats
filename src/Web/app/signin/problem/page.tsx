import Link from "next/link";
import { getTranslations } from "next-intl/server";

const reasons = ["no_access", "tenant_suspended", "idp_unavailable", "not_configured", "failed"] as const;
type Reason = (typeof reasons)[number];

/** Sign-in outcomes the API redirects to (spec 003 section 10). The reason is a code, never free text. */
export default async function SignInProblemPage({ searchParams }: { searchParams: Promise<{ reason?: string }> }) {
  const t = await getTranslations("signin_problem");
  const { reason: raw } = await searchParams;
  const reason: Reason = reasons.includes(raw as Reason) ? (raw as Reason) : "failed";
  const retry = reason === "failed" || reason === "idp_unavailable";
  return (
    <main id="main" className="page narrow">
      <h1>{t(`${reason}.title`)}</h1>
      <p>{t(`${reason}.body`)}</p>
      {retry ? (
        <Link className="button" href="/signin">
          {t("retry")}
        </Link>
      ) : null}
    </main>
  );
}
