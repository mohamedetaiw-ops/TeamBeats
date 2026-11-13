import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type SignInOptions } from "@/lib/api";
import type { KioskDeviceInfo } from "@/lib/kiosk";
import { DevSignInForm, type DevSignInMode } from "./DevSignInForm";
import { OfflineWipe } from "@/features/app/OfflineSync";

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

/**
 * A page on this site to open after signing in, such as the authorization request of an app (spec 035 FR-003).
 * Anything else, another site or an API path, is dropped; the API applies the same rule to the single sign-on return.
 */
function safeReturn(value: string | string[] | undefined): string | null {
  const path = Array.isArray(value) ? value[0] : value;
  if (!path || path.length > 2048 || !path.startsWith("/") || path.startsWith("//") || path.startsWith("/\\") || path.startsWith("/api/")) {
    return null;
  }
  return path;
}

export default async function SignInPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const returnTo = safeReturn((await searchParams).returnTo);
  // An enrolled kiosk signs people in with badges only (spec 030 US4): every way to the sign-in page leads to its start screen.
  const kiosk = await apiGet<KioskDeviceInfo>("/api/v1/kiosk/device");
  if (kiosk.ok && kiosk.data.enrolled) {
    redirect("/kiosk");
  }
  const t = await getTranslations("signin");
  const mode = devSignInMode();
  const options = await apiGet<SignInOptions>("/api/v1/auth/signin/options");
  const sso = options.ok && options.data.sso;
  return (
    <main id="main" className="page narrow">
      <h1>{t("title")}</h1>
      <OfflineWipe />
      {sso ? (
        <a className="button" href={returnTo ? `/api/v1/auth/signin/sso?returnTo=${encodeURIComponent(returnTo)}` : "/api/v1/auth/signin/sso"}>
          {t("ssoMicrosoft")}
        </a>
      ) : null}
      {mode ? <DevSignInForm mode={mode} testAccount={process.env.TEAMBEATS_TEST_ACCOUNT || "admin"} returnTo={returnTo} /> : null}
      {!sso && !mode ? <p>{t("ssoPending")}</p> : null}
    </main>
  );
}
