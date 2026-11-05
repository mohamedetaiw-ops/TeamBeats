import { getTranslations } from "next-intl/server";
import { apiGet, type SignInOptions } from "@/lib/api";
import { DevSignInForm } from "./DevSignInForm";

/**
 * Sign-in entry. Single sign-on starts on the API, which redirects to the tenant's identity provider (spec 003 US2).
 * Development sign-in renders only when the development host is in use (ADR-0004).
 */
export default async function SignInPage() {
  const t = await getTranslations("signin");
  const devSignIn = process.env.TEAMBEATS_DEV_SIGNIN === "true" && process.env.NODE_ENV !== "production";
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
      {devSignIn ? <DevSignInForm /> : null}
      {!sso && !devSignIn ? <p>{t("ssoPending")}</p> : null}
    </main>
  );
}
