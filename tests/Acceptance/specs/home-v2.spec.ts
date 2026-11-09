import { readFileSync } from "node:fs";
import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * The v2 home as staging seeds it (src/Api.DevHost/seeds/staging-home.json): the file's placements are published on a
 * fresh tenant, which also checks the file against the component manifests, and the home shows the task summary and My
 * services of spec 016 next to news, the tool box and the call to action band (the news, services and menu the seeder
 * also publishes are covered by tests/Integration/StagingHomeSeedTests.cs), in English and Arabic with axe.
 */
type Seed = { template: string; regions: Record<string, { id: string; componentRef: string; span?: number; props?: Record<string, unknown> }[]> };
const seed = JSON.parse(readFileSync(new URL("../../../src/Api.DevHost/seeds/staging-home.json", import.meta.url), "utf8")) as Seed;
// The seeder swaps "seed:news:<key>" for the address of the story it published; this test publishes no news, so the
// placement links to the home page instead.
for (const placements of Object.values(seed.regions)) {
  for (const p of placements) {
    for (const [key, value] of Object.entries(p.props ?? {})) {
      if (typeof value === "string" && value.startsWith("seed:news:")) p.props![key] = "/";
    }
  }
}

async function seedHome(tenant: TestTenant) {
  const api = await tenantApi(tenant);
  const page = await createPage(api, seed.template, `home-${Date.now().toString(36)}`);
  await saveAndPublish(api, page, page.draft.regions.map((r) => ({ ...r, placements: seed.regions[r.id] ?? [] })));
  const shell = await api.put("/api/v1/experience/shell/definition", {
    data: { start: ["identity", "global_navigation"], center: ["search"], end: ["settings"], below: ["breadcrumbs"], utilities: [], homePageId: page.id },
  });
  expect(shell.ok(), await shell.text()).toBe(true);
  await api.dispose();
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`v2 home in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`v2home-${locale}`, locale, "workplace_plus");
      await seedHome(tenant);
    });

    test("the seeded home shows tasks, services, the tool box and the call to action", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/`);
      const tasks = page.getByTestId("home-task-summary");
      await expect(tasks.getByRole("heading", { name: locale === "ar" ? "يومك" : "Your day" })).toBeVisible();
      await expect(tasks.getByText(msg(locale, "services.home.tasks.none"))).toBeVisible();
      await expect(tasks.getByRole("link", { name: msg(locale, "services.home.tasks.inbox") })).toHaveAttribute("href", "/tasks");
      const services = page.getByTestId("home-my-services");
      await expect(services.getByRole("heading", { name: msg(locale, "services.home.services.title") })).toBeVisible();
      await expect(services.getByRole("link").first()).toBeVisible();
      await expect(page.getByRole("link", { name: locale === "ar" ? "تصفح الخدمات" : "Browse services" })).toHaveAttribute("href", "/services");
      await expectAccessible(page, testInfo, `home-v2-${locale}`);
      await page.screenshot({ path: testInfo.outputPath(`home-v2-${locale}.png`), fullPage: true });
    });
  });
}

const paragraph = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });

async function publishNews(api: APIRequestContext, en: string, ar: string) {
  const created = await api.post("/api/v1/content/items", {
    data: { type: "news", variants: { en: { title: en, body: paragraph(en) }, ar: { title: ar, body: paragraph(ar) } } },
  });
  expect(created.status(), await created.text()).toBe(201);
  const id = ((await created.json()) as { id: string }).id;
  const published = await api.post(`/api/v1/content/items/${id}/publish`, { data: {} });
  expect(published.ok(), await published.text()).toBe(true);
}

test.describe("v2 home language switch", () => {
  test.use({ locale: "en-US" });
  let tenant: TestTenant;

  test.beforeAll(async () => {
    tenant = await createTenant("v2home-switch", "en", "workplace_plus");
    await seedHome(tenant);
    const api = await tenantApi(tenant);
    await publishNews(api, "Shuttle times change on Monday", "تتغير مواعيد الحافلة يوم الاثنين");
    await api.dispose();
  });

  test("switching language shows the news in the new language without a reload", async ({ page }) => {
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.goto(`${hostOrigin(tenant.key)}/`);
    await expect(page.getByRole("link", { name: "Shuttle times change on Monday" })).toBeVisible();
    await page.getByRole("button", { name: msg("en", "shell.switchLanguage", { language: msg("en", "locale.ar") }) }).click();
    await expect(page.getByRole("link", { name: "تتغير مواعيد الحافلة يوم الاثنين" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Shuttle times change on Monday" })).toHaveCount(0);
  });
});
