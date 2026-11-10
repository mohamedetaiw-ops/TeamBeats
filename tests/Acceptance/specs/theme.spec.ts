import { expect, signInWithDevForm, test } from "../support/fixtures";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { msg } from "../support/messages";
import { hostOrigin } from "../support/stack";

/**
 * Spec 033 T010: the web app reads only the generated --tb-* tokens. Signed-out pages keep the platform defaults, a
 * signed-in page takes the tenant theme in light and dark, and the theme loads before the first paint, so it causes
 * no layout shift.
 */
const accent = (page: import("@playwright/test").Page) =>
  page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue("--tb-color-accent-base").trim());

test.describe("tenant theme", () => {
  test.describe.configure({ mode: "serial" });
  test.use({ locale: "en-US" });

  let tenant: TestTenant;

  test.beforeAll(async () => {
    tenant = await createTenant("theme", "en");
    const api = await tenantApi(tenant);
    // Packages allow one brand, the default one seeded from tenant settings; a new version of it changes the accent.
    const brands = (await (await api.get("/api/v1/experience/brands")).json()) as { id: string }[];
    const edited = await api.put(`/api/v1/experience/brands/${brands[0]!.id}`, {
      data: { name: "Default", tokens: { "color.accent.base": { $value: "#0f766e", $modes: { dark: "#5eead4" } } } },
    });
    expect(edited.ok(), await edited.text()).toBe(true);
    await api.dispose();
  });

  test("signed-out pages use the platform tokens", async ({ page }) => {
    await page.goto(`${hostOrigin(tenant.key)}/signin`);
    expect(await accent(page)).toBe("#4338ca");
    await expect(page.locator('link[href*="theme.css"]')).toHaveCount(0);
  });

  test("signed-in pages use the tenant theme in light and dark without layout shift", async ({ page }) => {
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.goto(`${hostOrigin(tenant.key)}/`);
    expect(await accent(page)).toBe("#0f766e");
    // The first filled link button; quiet ones such as the header task badge have no fill.
    const button = page.locator("a.button:not(.button-quiet)").first();
    if ((await button.count()) > 0) {
      await expect(button).toHaveCSS("background-color", "rgb(15, 118, 110)");
    }
    const shift = await page.evaluate(
      () =>
        new Promise<number>((resolve) => {
          let total = 0;
          new PerformanceObserver((list) => {
            for (const entry of list.getEntries() as (PerformanceEntry & { value: number; hadRecentInput: boolean })[]) {
              if (!entry.hadRecentInput) total += entry.value;
            }
          }).observe({ type: "layout-shift", buffered: true });
          setTimeout(() => resolve(total), 500);
        }),
    );
    expect(shift).toBeLessThan(0.1);

    // Light is the default mode, so a dark system setting changes nothing until the person picks "system" or "dark".
    await page.emulateMedia({ colorScheme: "dark" });
    expect(await accent(page)).toBe("#0f766e");
    await page.getByRole("button", { name: msg("en", "shell.settings.open") }).click();
    await page.getByRole("radiogroup", { name: msg("en", "shell.settings.mode.label") }).getByText(msg("en", "shell.settings.mode.system"), { exact: true }).click();
    await expect.poll(() => accent(page)).toBe("#5eead4");
  });
});
