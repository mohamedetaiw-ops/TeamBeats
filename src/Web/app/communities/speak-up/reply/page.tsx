import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { SpeakUpReply } from "@/features/engagement/SpeakUpReply";

/** The anonymous Speak Up author's one-time reply page (spec 023 US2). The token never reaches this server render. */
export default async function SpeakUpReplyPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("engagement");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: "/communities" },
          { label: t("speakUp.replyTitle"), href: "/communities/speak-up/reply" },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("speakUp.replyTitle")} description={t("speakUp.replyDescription")} icon="lock" />
        <SpeakUpReply />
      </main>
    </>
  );
}
