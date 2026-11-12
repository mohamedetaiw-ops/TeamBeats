import { getTranslations } from "next-intl/server";
import { ButtonLink } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";

/**
 * The page the installed app shows when there is no connection (spec 030 US1, US3 scenario 2). The service worker keeps
 * a copy; it holds nothing about the person, so it is the same for everyone on this tenant host.
 */
export const dynamic = "force-static";

export default async function OfflinePage() {
  const t = await getTranslations("app.offline");
  return (
    <main id="main" className="page">
      <StateView
        kind="offline"
        title={t("title")}
        message={t("message")}
        action={
          <ButtonLink href="/" variant="primary" icon="home">
            {t("retry")}
          </ButtonLink>
        }
      />
    </main>
  );
}
