import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 008 T014: the tool box utilities as platform components on a published page, in English and Arabic with axe.
 * The world clock shows its configured zones, the converter turns a time in one zone into another, and weather shows
 * the unsupported state until a provider is chosen.
 */
const unique = (prefix: string) => `${prefix}-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`tool box in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US", timezoneId: "Asia/Dubai" });
    let tenant: TestTenant;
    let slug: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`toolbox-${locale}`, locale);
      const api = await tenantApi(tenant);
      const page = await createPage(api, "T01", unique("tools"));
      await saveAndPublish(api, page, page.draft.regions.map((r) => ({
        ...r,
        placements: [
          { id: "clock", componentRef: "toolbox.world_clock@1", props: { zones: ["Asia/Dubai", "Europe/London", "Not/AZone"] } },
          { id: "converter", componentRef: "toolbox.time_converter@1" },
          { id: "weather", componentRef: "toolbox.weather@1" },
        ],
      })));
      slug = page.slug;
      await api.dispose();
    });

    test("the world clock, time converter and weather work as platform components", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/p/${slug}`);

      // A zone the browser does not know is left out.
      const clock = page.getByRole("region", { name: msg(locale, "toolbox.clock.title") });
      await expect(clock.getByRole("listitem")).toHaveCount(2);

      const weather = page.getByRole("region", { name: msg(locale, "toolbox.weather.title") });
      await expect(weather).toContainText(msg(locale, "toolbox.weather.unsupported"));

      const converter = page.getByRole("region", { name: msg(locale, "toolbox.converter.title") });
      await expect(converter.getByRole("status")).toHaveText(msg(locale, "toolbox.converter.empty"));
      await converter.getByLabel(msg(locale, "toolbox.converter.when")).fill("2026-01-15T09:00");
      await converter.getByLabel(msg(locale, "toolbox.converter.from")).selectOption("Asia/Dubai");
      await converter.getByLabel(msg(locale, "toolbox.converter.to")).selectOption("UTC");
      // 09:00 in Dubai (UTC+4) is 05:00 UTC; the expected text uses the browser's own formatting.
      const expected = await page.evaluate((l) => new Intl.DateTimeFormat(l, { timeZone: "UTC", dateStyle: "full", timeStyle: "short" }).format(Date.UTC(2026, 0, 15, 5, 0)), locale);
      await expect(converter.getByRole("status")).toHaveText(msg(locale, "toolbox.converter.result", { time: expected }));

      await expectAccessible(page, testInfo, `toolbox-${locale}`);
    });
  });
}
