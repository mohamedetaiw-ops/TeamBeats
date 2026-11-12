import { headers } from "next/headers";
import { getTranslations } from "next-intl/server";
import { ButtonLink } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";

/**
 * The page the installed app shows when there is no connection (spec 030 US1, US3 scenario 2). The service worker keeps
 * a copy; it holds nothing about the person, so it is the same for everyone on this tenant host. The items kept for
 * offline reading are decrypted and shown in the browser by public/offline-reader.js.
 */
export default async function OfflinePage() {
  const t = await getTranslations("app.offline");
  // Rendered per request so the reader script carries this response's nonce; the service worker keeps the page and its
  // policy together, so the pair still matches with no connection.
  const nonce = (await headers()).get("x-nonce") ?? undefined;
  return (
    <main id="main" className="page">
      {/* Items kept for offline reading appear here; the reader script needs no connection (spec 030 US3). */}
      <div
        id="offline-reader"
        data-title={t("library.title")}
        data-banner={t("library.banner", { time: "{time}" })}
        data-back={t("library.back")}
        data-announcement={t("library.announcement")}
        data-policy={t("library.policy")}
      />
      <script src="/offline-reader.js" defer nonce={nonce} />
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
