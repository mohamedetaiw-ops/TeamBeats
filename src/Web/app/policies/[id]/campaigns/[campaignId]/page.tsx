import { CampaignsPage } from "@/features/policies/CampaignsPage";

/** The campaigns page with one campaign marked, where notices about a campaign link to (spec 025 US2). */
export default async function PolicyCampaignPage({ params }: { params: Promise<{ id: string; campaignId: string }> }) {
  const { id, campaignId } = await params;
  return <CampaignsPage id={id} focus={campaignId} />;
}
