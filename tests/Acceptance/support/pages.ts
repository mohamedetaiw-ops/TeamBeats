import { request, type APIRequestContext } from "@playwright/test";
import { stack } from "./stack";
import type { TestTenant } from "./platform";

/**
 * Page setup through the spec 032 API as the tenant administrator: create from a template, fill regions with
 * reference components the picker allows, save the draft and publish. Reference packs exist in the development host.
 */
export type Placement = { id: string; componentRef: string; props?: Record<string, unknown>; span?: number };

type Region = { id: string; placements: Placement[] } & Record<string, unknown>;
type PageView = { id: string; slug: string; eTag: string; draft: { regions: Region[] } & Record<string, unknown> };
type PickerItem = { componentRef: string; eligible: boolean; category: string };

export async function tenantApi(tenant: TestTenant): Promise<APIRequestContext> {
  const host = `${tenant.key}.${stack.baseDomain}`;
  const anonymous = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
  const response = await anonymous.post("/dev/signin", { data: { email: tenant.adminEmail } });
  const token = /tb_session=([^;]+)/.exec(response.headers()["set-cookie"] ?? "")?.[1];
  await anonymous.dispose();
  if (!response.ok() || !token) {
    throw new Error(`Tenant sign-in failed with ${response.status()}.`);
  }
  return request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host, cookie: `tb_session=${token}` } });
}

const bilingual = (en: string, ar: string) => ({ en, ar });

/** Valid properties for each reference component used on generated pages. */
export const referenceProps: Record<string, Record<string, unknown>> = {
  "reference.alpha.text@1": { heading: bilingual("Safety first", "السلامة أولا"), body: bilingual("Wear a helmet on the plant floor.", "ارتد الخوذة في أرض المصنع.") },
  "reference.beta.banner@1": { image: "asset:3f2b8c1e-6d4a-4f7e-9a1b-2c3d4e5f6a7b", alt: bilingual("Plant at sunrise", "المصنع عند الشروق") },
  "reference.alpha.kpi@1": { label: bilingual("Uptime", "وقت التشغيل"), value: 99.5, unit: "percent" },
  "reference.beta.list@1": { pageSize: 5 },
  "reference.alpha.links@1": { title: "Links", links: ["https://example.com/handbook", "/news"] },
};

export async function createPage(api: APIRequestContext, templateId: string, slug: string): Promise<PageView> {
  const response = await api.post("/api/v1/experience/pages", { data: { templateId, slug } });
  if (response.status() !== 201) {
    throw new Error(`Page creation failed with ${response.status()}: ${await response.text()}`);
  }
  return (await response.json()) as PageView;
}

export async function saveAndPublish(api: APIRequestContext, page: PageView, regions: Region[]): Promise<void> {
  const draft = { ...page.draft, regions };
  const saved = await api.put(`/api/v1/experience/pages/${page.id}/draft`, { data: { definition: draft }, headers: { "if-match": page.eTag } });
  if (!saved.ok()) {
    throw new Error(`Draft save failed with ${saved.status()}: ${await saved.text()}`);
  }
  const published = await api.post(`/api/v1/experience/pages/${page.id}/publish`, { data: { reason: "e2e" } });
  if (!published.ok()) {
    throw new Error(`Publish failed with ${published.status()}: ${await published.text()}`);
  }
}

/**
 * Fills every region with up to two eligible reference components, one from each pack when the region allows it, so
 * both packs appear on every template (swap test S02).
 */
export async function fillWithReferencePacks(api: APIRequestContext, page: PageView, maxPerRegion: Record<string, number>): Promise<Region[]> {
  const regions: Region[] = [];
  for (const region of page.draft.regions) {
    const picker = (await (await api.get(`/api/v1/experience/pages/${page.id}/components?region=${region.id}`)).json()) as PickerItem[];
    const eligible = new Set(picker.filter((p) => p.eligible).map((p) => p.componentRef));
    const candidates = Object.keys(referenceProps).filter((ref) => eligible.has(ref));
    const alpha = candidates.find((ref) => ref.startsWith("reference.alpha."));
    const beta = candidates.find((ref) => ref.startsWith("reference.beta."));
    const chosen = [alpha, beta].filter((ref): ref is string => !!ref).slice(0, maxPerRegion[region.id] ?? 2);
    regions.push({
      ...region,
      placements: chosen.map((ref, i) => ({ id: `${region.id}-${i + 1}`, componentRef: ref, props: referenceProps[ref] })),
    });
  }
  return regions;
}
