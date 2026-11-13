import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { StateView } from "@/components/ui/StateView";
import { AppConsent, type ConsentPrompt } from "@/features/api-access/AppConsent";
import "@/features/api-access/api-access.css";

// The person and the app decide what this page shows, so it is read on every request.
export const dynamic = "force-dynamic";

/**
 * The consent screen of the tenant's authorization server (spec 035 US2, FR-013). The authorization endpoint sends the
 * browser here with its own query when the person has not yet consented to everything the app asks; after the decision
 * the browser goes back to the same request. The API checks the app and its redirect address again.
 */
export default async function AppConsentPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const params = await searchParams;
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (typeof value === "string" && key !== "tb_deny") query.set(key, value);
  }
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect(`/signin?returnTo=${encodeURIComponent(`/app-consent?${query.toString()}`)}`);
  }
  const t = await getTranslations("apiAccess.consent");
  const lookup = new URLSearchParams({
    client_id: query.get("client_id") ?? "",
    redirect_uri: query.get("redirect_uri") ?? "",
    scope: query.get("scope") ?? "",
  });
  const prompt = await apiGet<ConsentPrompt>(`/api/v1/me/app-consent?${lookup.toString()}`);
  return (
    <main id="main" className="page narrow">
      {prompt.ok ? (
        <AppConsent prompt={prompt.data} query={query.toString()} />
      ) : (
        <>
          <h1>{t("unknownTitle")}</h1>
          <StateView kind="error" title={t("unknownTitle")} message={t("unknown")} />
        </>
      )}
    </main>
  );
}
