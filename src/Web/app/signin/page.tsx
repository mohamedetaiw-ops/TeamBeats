import { getTranslations } from "next-intl/server";
import { DevSignInForm, type DevSignInMode } from "./DevSignInForm";

// Read the sign-in mode at request time, so one web image serves staging and production.
export const dynamic = "force-dynamic";

/**
 * Sign-in entry. Development sign-in renders only when the development host is in use (ADR-0004):
 * TEAMBEATS_DEV_SIGNIN=open (or true) outside production builds, or =password for the staging test account (spec 003 US7).
 * Showing the form grants nothing: the production API has no /dev routes.
 */
function devSignInMode(): DevSignInMode | null {
  const value = process.env.TEAMBEATS_DEV_SIGNIN;
  if (value === "password") return "password";
  if ((value === "true" || value === "open") && process.env.NODE_ENV !== "production") return "open";
  return null;
}

export default async function SignInPage() {
  const t = await getTranslations("signin");
  const mode = devSignInMode();
  return (
    <main id="main" className="page narrow">
      <h1>{t("title")}</h1>
      {mode ? <DevSignInForm mode={mode} /> : <p>{t("ssoPending")}</p>}
    </main>
  );
}
