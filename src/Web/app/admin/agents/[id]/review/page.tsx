import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText, type AgentReview } from "@/lib/agents/types";
import { AgentTabs } from "@/features/agents/AgentTabs";
import { ReviewPanel } from "@/features/agents/ReviewPanel";
import { agentsSection } from "@/features/agents/guard";
import "@/features/agents/agents.css";

/** The review page (spec 020 US2, T014), opened from the publisher's inbox task. With nothing in review it is not found. */
export default async function AgentReviewPage({ params }: { params: Promise<{ id: string }> }) {
  await agentsSection();
  const { id } = await params;
  const review = await apiGet<AgentReview>(`/api/v1/agents/${encodeURIComponent(id)}/review`);
  if (!review.ok) notFound();
  const t = await getTranslations("agents.review");
  const locale = await getLocale();
  return (
    <>
      <h1>{t("title", { name: pickText(review.data.definition.name, locale) })}</h1>
      <AgentTabs current="agents" />
      <ReviewPanel review={review.data} />
    </>
  );
}
