import { getTranslations } from "next-intl/server";
import { apiGet, type SignInOptions } from "@/lib/api";
import { DevSignInForm, type DevSignInMode } from "./DevSignInForm";

// Read the sign-in mode at request time, so one web image serves staging and production.
export const dynamic = "force-dynamic";

/**
 * Sign-in entry. Single sign-on starts on the API, which redirects to the tenant's identity provider (spec 003 US2).
 * Development sign-in renders only when the development host is in use (ADR-0004):
 * TEAMBEATS_DEV_SIGNIN=open (or true) outside production builds, or =testaccount for the one-click staging test account
 * (spec 003 US7, account name from TEAMBEATS_TEST_ACCOUNT, default "admin").
 * Showing the form grants nothing: the production API has no /dev routes.
 */
function devSignInMode(): DevSignInMode | null {
  const value = process.env.TEAMBEATS_DEV_SIGNIN;
  if (value === "testaccount") return "testaccount";
  if ((value === "true" || value === "open") && process.env.NODE_ENV !== "production") return "open";
  return null;
}

export default async function SignInPage() {
  const t = await getTranslations("signin");
  const mode = devSignInMode();
  const options = await apiGet<SignInOptions>("/api/v1/auth/signin/options");
  const sso = options.ok && options.data.sso;
  return (
    <main id="main" className="page narrow">
      <h1>{t("title")}</h1>
      {sso ? (
        <a className="button" href="/api/v1/auth/signin/sso">
          {t("ssoMicrosoft")}
        </a>
      ) : null}
      {mode ? <DevSignInForm mode={mode} testAccount={process.env.TEAMBEATS_TEST_ACCOUNT || "admin"} /> : null}
      {!sso && !mode ? <p>{t("ssoPending")}</p> : null}
    </main>
  );
}
