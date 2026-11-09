import { redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import type { Conversation, ConversationSummary } from "@/lib/assistant/types";
import { AssistantChat } from "@/features/assistant/Assistant";
import { TextTools } from "@/features/assistant/TextActions";
import "@/features/assistant/assistant.css";

const ID = /^[0-9a-f-]{36}$/i;

/**
 * The assistant page (spec 014 FR-001, US1 and US4, T007): the person's own conversations and the open one. The API
 * decides everything: whether the assistant is available, what may be retrieved and cited, and whose history this is.
 */
export default async function AssistantPage({ searchParams }: { searchParams: Promise<{ c?: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("assistant");
  const locale = await getLocale();
  const { c } = await searchParams;
  const [list, open] = await Promise.all([
    apiGet<{ conversations: ConversationSummary[] }>("/api/v1/assistant/conversations"),
    c && ID.test(c) ? apiGet<Conversation>(`/api/v1/assistant/conversations/${c}`) : Promise.resolve(null),
  ]);

  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/assistant" }]} />
      <main id="main" className="page assistant-page">
        <h1>{t("title")}</h1>
        {!list.ok ? (
          <p className="notice" role="alert">
            {list.status === 403 ? t("notAvailable") : t("failed")}
          </p>
        ) : (
          <>
            {open && !open.ok ? (
              <p className="notice" role="alert">
                {t("notFound")}
              </p>
            ) : null}
            <AssistantChat
              key={open?.ok ? open.data.id : "new"}
              initial={open?.ok ? open.data : null}
              conversations={list.data.conversations}
            />
            <TextTools uiLocale={locale} />
          </>
        )}
      </main>
    </>
  );
}
