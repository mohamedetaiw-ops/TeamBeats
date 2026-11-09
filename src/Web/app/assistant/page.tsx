import Link from "next/link";
import { redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import type { AssistantScope, Conversation, ConversationSummary } from "@/lib/assistant/types";
import { nameIn, policyHref, type PolicyView } from "@/lib/policies/types";
import { AssistantChat } from "@/features/assistant/Assistant";
import { TextTools } from "@/features/assistant/TextActions";
import "@/features/assistant/assistant.css";

const ID = /^[0-9a-f-]{36}$/i;

/**
 * The assistant page (spec 014 FR-001, US1 and US4, T007): the person's own conversations and the open one. The API
 * decides everything: whether the assistant is available, what may be retrieved and cited, and whose history this is.
 * With <c>?policy=</c> a new conversation is kept to that policy ("Ask about this policy", spec 025 FR-011); an open
 * conversation shows the scope the API keeps for it.
 */
export default async function AssistantPage({ searchParams }: { searchParams: Promise<{ c?: string; policy?: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("assistant");
  const locale = await getLocale();
  const { c, policy } = await searchParams;
  const [list, open] = await Promise.all([
    apiGet<{ conversations: ConversationSummary[] }>("/api/v1/assistant/conversations"),
    c && ID.test(c) ? apiGet<Conversation>(`/api/v1/assistant/conversations/${c}`) : Promise.resolve(null),
  ]);
  const wanted: AssistantScope | null = open?.ok
    ? open.data.scope
    : !c && policy && ID.test(policy)
      ? { source: "policies", itemId: policy }
      : null;
  const scoped = wanted ? await apiGet<PolicyView>(`/api/v1/policies/${encodeURIComponent(wanted.itemId)}`) : null;
  const scope = wanted && scoped?.ok ? wanted : null;

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
            {wanted && !open && !scoped?.ok ? (
              <p className="notice" role="alert">
                {t("scope.notFound")}
              </p>
            ) : null}
            {scope && scoped?.ok ? (
              <p className="notice" data-testid="assistant-scope">
                {t.rich("scope.policy", {
                  name: () => (
                    <Link href={policyHref(scoped.data.id)}>
                      <bdi>{nameIn(scoped.data.names, locale)}</bdi>
                    </Link>
                  ),
                })}
              </p>
            ) : null}
            <AssistantChat
              key={open?.ok ? open.data.id : scope ? `policy-${scope.itemId}` : "new"}
              initial={open?.ok ? open.data : null}
              conversations={list.data.conversations}
              scope={scope}
            />
            <TextTools uiLocale={locale} />
          </>
        )}
      </main>
    </>
  );
}
