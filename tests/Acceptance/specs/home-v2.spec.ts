import { readFileSync } from "node:fs";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * The v2 home as staging seeds it (src/Api.DevHost/seeds/staging-home.json): the file's placements are published on a
 * fresh tenant, which also checks the file against the component manifests, and the home shows the task summary and My
 * services of spec 016 next to news, the tool box and the call to action band, in English and Arabic with axe.
 */
type Seed = { template: string; regions: Record<string, { id: string; componentRef: string; span?: number; props?: Record<string, unknown> }[]> };
const seed = JSON.parse(readFileSync(new URL("../../../src/Api.DevHost/seeds/staging-home.json", import.meta.url), "utf8")) as Seed;

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
