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

/** A session of the tenant administrator from the development sign-in, for a browser cookie or API calls. */
export async function sessionToken(tenant: TestTenant): Promise<string> {
  const host = `${tenant.key}.${stack.baseDomain}`;
  const anonymous = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
  const response = await anonymous.post("/dev/signin", { data: { email: tenant.adminEmail } });
  const token = /tb_session=([^;]+)/.exec(response.headers()["set-cookie"] ?? "")?.[1];
  await anonymous.dispose();
  if (!response.ok() || !token) {
    throw new Error(`Tenant sign-in failed with ${response.status()}.`);
  }
  return token;
}

/** An API context signed in as another person of the tenant, by email, through the development sign-in. */
export async function tenantApiAs(tenant: TestTenant, email: string): Promise<APIRequestContext> {
  const host = `${tenant.key}.${stack.baseDomain}`;
  const anonymous = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
  const response = await anonymous.post("/dev/signin", { data: { email } });
  const token = /tb_session=([^;]+)/.exec(response.headers()["set-cookie"] ?? "")?.[1];
  await anonymous.dispose();
  if (!response.ok() || !token) throw new Error(`Sign-in failed with ${response.status()}.`);
  return request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host, cookie: `tb_session=${token}` } });
}

/**
 * Publishes an agent through the API (spec 020): the tenant administrator installs the template in the unit and saves
 * the definition with the given changes, submits it, and a second administrator overrides the incomplete gate and
 * publishes. Returns the agent ID.
 */
export async function publishAgentThroughApi(tenant: TestTenant, reviewerEmail: string, unitId: string, changes: Record<string, unknown>): Promise<string> {
  const api = await tenantApi(tenant);
  const installed = await api.post("/api/v1/agents", { data: { templateId: "enterprise_knowledge", orgUnitId: unitId } });
  if (installed.status() !== 201) throw new Error(`Install failed with ${installed.status()}: ${await installed.text()}`);
  const detail = (await installed.json()) as { agent: { id: string }; definition: Record<string, unknown> };
  const id = detail.agent.id;
  const saved = await api.put(`/api/v1/agents/${id}/draft`, { data: { revision: 1, definition: { ...detail.definition, ...changes } } });
  if (!saved.ok()) throw new Error(`Save failed with ${saved.status()}: ${await saved.text()}`);
  const submitted = await api.post(`/api/v1/agents/${id}/submit`, { data: { revision: 2 } });
  if (!submitted.ok()) throw new Error(`Submit failed with ${submitted.status()}: ${await submitted.text()}`);
  await api.dispose();
  // The gate ends incomplete for the template's few cases, on the workers; the reviewer overrides it and publishes.
  const reviewer = await tenantApiAs(tenant, reviewerEmail);
  let runId: string | null | undefined;
  for (let i = 0; i < 90 && !runId; i++) {
    const review = (await (await reviewer.get(`/api/v1/agents/${id}/review`)).json()) as { gate: { status: string; runId?: string | null } };
    if (review.gate.status === "incomplete") runId = review.gate.runId;
    else await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  if (!runId) throw new Error("The release gate did not finish.");
  const overridden = await reviewer.post(`/api/v1/evaluation/gates/${runId}/override`, { data: { reason: "Checked by hand before publishing." } });
  if (!overridden.ok()) throw new Error(`Override failed with ${overridden.status()}: ${await overridden.text()}`);
  const published = await reviewer.post(`/api/v1/agents/${id}/publish`, { data: { version: 1 } });
  if (!published.ok()) throw new Error(`Publish failed with ${published.status()}: ${await published.text()}`);
  await reviewer.dispose();
  return id;
}

export async function tenantApi(tenant: TestTenant): Promise<APIRequestContext> {
  const host = `${tenant.key}.${stack.baseDomain}`;
  const token = await sessionToken(tenant);
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
