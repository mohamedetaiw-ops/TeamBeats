import { expect, signInWithDevForm, test } from "../support/fixtures";
import { chooseDesign, chooseMode, expectConformant, settle } from "../support/a11y";
import { seedHome } from "../support/home";
import type { LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";
import { routes } from "./routes";

/**
 * The accessibility conformance gate (spec 038 US1, A11Y-005 and A11Y-006): zero critical and zero serious axe
 * violations with the WCAG 2.0, 2.1 and 2.2 A and AA rules.
 *  - Primary pages: all 32 combinations of four designs, light and dark, Arabic and English, desktop and phone.
 *  - Other routes: once per language, with the design, mode and width rotated over the inventory, so every value of
 *    every dimension is covered on many routes.
 *  - Public routes: signed out, in both languages, both colour schemes and both widths (no design applies signed out).
 * Findings are soft, so one run lists every page and combination that fails.
 */
const designs = ["beats", "atlas", "pulse", "studio"] as const;
const locales = ["en", "ar"] as const satisfies readonly LaunchLocale[];
const modes = ["light", "dark"] as const;
const widths = [
  { name: "desktop", width: 1280, height: 900 },
  { name: "phone", width: 390, height: 844 },
] as const;

const primary = routes.filter((r) => r.tier === "primary" && !r.public).map((r) => r.path);
const others = routes.filter((r) => r.tier === "other" && !r.public).map((r) => r.path);
const publicRoutes = routes.filter((r) => r.public && (r.tier === "primary" || r.tier === "other")).map((r) => r.path);

/** The design, mode and width each other route takes: designs first, then modes, then widths, round robin. */
const rotation = others.map((path, i) => ({
  path,
  design: designs[i % designs.length]!,
  mode: modes[Math.floor(i / designs.length) % modes.length]!,
  width: widths[Math.floor(i / (designs.length * modes.length)) % widths.length]!,
}));

for (const design of designs) {
  for (const locale of locales) {
    test.describe(`${design} design in ${locale}`, () => {
      // Default mode: a failing combination still lets the others run, each worker with its own tenant.
      test.describe.configure({ mode: "default" });
      test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      let tenant: TestTenant;

      test.beforeAll(async () => {
        tenant = await createTenant(`a11y-${design}-${locale}`, locale, "enterprise_gov");
        await seedHome(tenant);
        await chooseDesign(tenant, design);
      });

      for (const mode of modes) {
        test(`primary pages in ${mode} mode at desktop and phone width`, async ({ page }, testInfo) => {
          test.setTimeout(20 * 60_000);
          await chooseMode(tenant, mode);
          await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
          for (const path of primary) {
            await page.setViewportSize(widths[0]);
            await page.goto(`${hostOrigin(tenant.key)}${path}`);
            await expect(page.locator("html")).toHaveAttribute("data-design", design);
            await expect(page.locator("html")).toHaveAttribute("data-mode", mode);
            for (const width of widths) {
              await page.setViewportSize(width);
              await settle(page);
              await expectConformant(page, testInfo, `${design}-${mode}-${locale}-${width.name}${path.replace(/\//g, "-")}`);
            }
          }
        });
      }

      test("other routes in their rotated mode and width", async ({ page }, testInfo) => {
        test.setTimeout(20 * 60_000);
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        for (const mode of modes) {
          const mine = rotation.filter((r) => r.design === design && r.mode === mode);
          if (mine.length === 0) continue;
          await chooseMode(tenant, mode);
          for (const { path, width } of mine) {
            await page.setViewportSize(width);
            await page.goto(`${hostOrigin(tenant.key)}${path}`);
            await settle(page);
            await expectConformant(page, testInfo, `${design}-${mode}-${locale}-${width.name}${path.replace(/\//g, "-")}`);
          }
        }
      });
    });
  }
}

for (const locale of locales) {
  test.describe(`signed-out pages in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });

    for (const scheme of modes) {
      test(`public routes in the ${scheme} colour scheme at desktop and phone width`, async ({ page }, testInfo) => {
        test.setTimeout(10 * 60_000);
        const tenant = await createTenant(`a11y-public-${locale}`, locale);
        await page.emulateMedia({ colorScheme: scheme });
        for (const path of publicRoutes) {
          await page.setViewportSize(widths[0]);
          await page.goto(`${hostOrigin(tenant.key)}${path}`);
          for (const width of widths) {
            await page.setViewportSize(width);
            await settle(page);
            await expectConformant(page, testInfo, `public-${scheme}-${locale}-${width.name}${path.replace(/\//g, "-")}`);
          }
        }
      });
    }
  });
}
