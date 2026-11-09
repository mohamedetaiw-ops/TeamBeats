import { CampaignsPage } from "@/features/policies/CampaignsPage";

/** Attestation campaigns of a policy (spec 025 US2). */
export default async function PolicyCampaignsPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <CampaignsPage id={id} focus={null} />;
}
