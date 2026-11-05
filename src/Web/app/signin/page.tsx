import { getTranslations } from "next-intl/server";
import { DevSignInForm } from "./DevSignInForm";

/** Sign-in entry. Development sign-in renders only when the development host is in use (ADR-0004). */
export default async function SignInPage() {
  const t = await getTranslations("signin");
  const devSignIn = process.env.TEAMBEATS_DEV_SIGNIN === "true" && process.env.NODE_ENV !== "production";
  return (
    <main id="main" className="page narrow">
      <h1>{t("title")}</h1>
      {devSignIn ? <DevSignInForm /> : <p>{t("ssoPending")}</p>}
    </main>
  );
}
