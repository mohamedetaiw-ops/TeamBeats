import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { seedHome } from "../support/home";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * The v2 home as staging seeds it (src/Api.DevHost/seeds/staging-home.json): the file's placements are published on a
 * fresh tenant, which also checks the file against the component manifests, and the home shows the task summary and My
 * services of spec 016 under the briefing, next to featured news, with the campaign band at the bottom (the news, services and menu the seeder
 * also publishes are covered by tests/Integration/StagingHomeSeedTests.cs), in English and Arabic with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`v2 home in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`v2home-${locale}`, locale, "workplace_plus");
      await seedHome(tenant);
    });

    test("the seeded home shows the briefing, your day, services, Ask TeamBeats and the campaign band last", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/`);
      const day = page.getByTestId("home-your-day");
      await expect(day.getByRole("heading", { name: msg(locale, "home.day.title") })).toBeVisible();
      await expect(day.getByText(msg(locale, "home.day.toApprove"))).toBeVisible();
      await expect(day.getByRole("link", { name: msg(locale, "home.day.inbox") })).toHaveAttribute("href", "/tasks");
      // This tenant has no Microsoft 365, so the agenda says so in one line instead of guessing meetings.
      await expect(day.getByText(msg(locale, "home.day.calendarUnavailable"))).toBeVisible();
      await expect(page.getByRole("link", { name: msg(locale, "shell.ask") })).toHaveAttribute("href", "/assistant");
      const services = page.getByTestId("home-my-services");
      await expect(services.getByRole("heading", { name: msg(locale, "services.home.services.title") })).toBeVisible();
      await expect(services.getByRole("link").first()).toBeVisible();
      const briefing = page.getByTestId("home-briefing");
      await expect(briefing.getByRole("heading", { level: 2 })).toBeVisible();
      await expect(briefing.getByText(msg(locale, "home.briefing.sources"))).toBeVisible();
      // The campaign band closes the page: after the news and services, never above them.
      const band = page.getByRole("link", { name: locale === "ar" ? "اطلع على البرنامج" : "See the programme" });
      // seedHome links the band to the home, as the test tenant has no seeded story (support/home.ts).
      await expect(band).toHaveAttribute("href", "/");
      const bandTop = (await band.boundingBox())!.y;
      expect(bandTop).toBeGreaterThan((await services.boundingBox())!.y);
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
