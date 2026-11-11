import { readFileSync } from "node:fs";
import { expect } from "@playwright/test";
import { createPage, saveAndPublish, tenantApi } from "./pages";
import type { TestTenant } from "./platform";

/**
 * Publishes the staging home (src/Api.DevHost/seeds/staging-home.json) on a test tenant and makes it the shell home,
 * as the v2 home and design system suites need a realistic page.
 */
type Seed = {
  template: string;
  navigation: { slug: string; labels: Record<string, string>; url: string }[];
  regions: Record<string, { id: string; componentRef: string; span?: number; props?: Record<string, unknown> }[]>;
  footer: unknown;
  views: { id: string; labels: Record<string, string>; slug: string; regions: Seed["regions"] }[];
};
const seed = JSON.parse(readFileSync(new URL("../../../src/Api.DevHost/seeds/staging-home.json", import.meta.url), "utf8")) as Seed;
// The seeder swaps "seed:news:<key>" for the address of the story it published; this test publishes no news, so the
// placement links to the home page instead.
for (const placements of [...Object.values(seed.regions), ...seed.views.flatMap((v) => Object.values(v.regions))]) {
  for (const p of placements) {
    for (const [key, value] of Object.entries(p.props ?? {})) {
      if (typeof value === "string" && value.startsWith("seed:news:")) p.props![key] = "/";
    }
  }
}

/**
 * With `extras`, also the file's footer and its two extra home views (spec 008 FR-056, FR-058), each view a page of
 * its own published from the same template.
 */
export async function seedHome(tenant: TestTenant, { extras = false }: { extras?: boolean } = {}) {
  const api = await tenantApi(tenant);
  const page = await createPage(api, seed.template, `home-${Date.now().toString(36)}`);
  await saveAndPublish(api, page, page.draft.regions.map((r) => ({ ...r, placements: seed.regions[r.id] ?? [] })));
  // The global menu the staging seeder publishes, so the shell shows its rail, top bar or sidebar.
  const items: { nodeId: string }[] = [];
  for (const entry of seed.navigation) {
    const nodeId = crypto.randomUUID();
    const node = await api.put(`/api/v1/experience/ia/${nodeId}`, {
      data: { labels: entry.labels, slug: `${entry.slug}-${Date.now().toString(36)}`, url: entry.url },
    });
    expect(node.ok(), await node.text()).toBe(true);
    items.push({ nodeId });
  }
  const menu = await api.put("/api/v1/experience/menus/global/tenant", { data: { items } });
  expect(menu.ok(), await menu.text()).toBe(true);
  const homeViews = [];
  for (const view of extras ? seed.views : []) {
    const viewPage = await createPage(api, seed.template, `${view.slug}-${Date.now().toString(36)}`);
    await saveAndPublish(api, viewPage, viewPage.draft.regions.map((r) => ({ ...r, placements: view.regions[r.id] ?? [] })));
    homeViews.push({ id: view.id, labels: view.labels, pageId: viewPage.id, audienceRuleId: null });
  }
  const shell = await api.put("/api/v1/experience/shell/definition", {
    data: {
      start: ["identity", "global_navigation"], center: ["search"], end: ["settings"], below: ["breadcrumbs"], utilities: [], homePageId: page.id,
      ...(extras ? { footer: seed.footer, homeViews } : {}),
    },
  });
  expect(shell.ok(), await shell.text()).toBe(true);
  await api.dispose();
}

