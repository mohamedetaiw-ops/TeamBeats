import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import AxeBuilder from "@axe-core/playwright";
import type { Locator, Page, TestInfo } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { seedHome } from "../support/home";
import type { LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Design system checks (spec 036, docs/design-system/testing.md) in each of the four designs and both directions:
 *  - the tenant's chosen design reaches the page (design and navigation attributes, fonts, accent);
 *  - accessibility: zero critical and zero serious axe violations on the home and the component gallery;
 *  - responsive layout: no horizontal scrolling at phone, tablet and desktop widths;
 *  - target size: every control is at least 24 by 24 CSS pixels (WCAG 2.2 SC 2.5.8);
 *  - visual regression: both component galleries match their reviewed baselines per design and direction.
 * Set DESIGN_SCREENSHOTS_DIR to also write full-page screenshots for design reviews (post-development fidelity check).
 */
const designs = [
  { key: "beats", navigation: "rail", accent: "#3b2fc9", font: "Readex Pro", arabicFont: "Readex Pro" },
  { key: "atlas", navigation: "top", accent: "#0b6e4f", font: "IBM Plex Sans", arabicFont: "IBM Plex Sans Arabic" },
  { key: "pulse", navigation: "sidebar", accent: "#c2185b", font: "Cairo", arabicFont: "Cairo" },
  // Inter has no Arabic, so Studio pairs it with IBM Plex Sans Arabic.
  { key: "studio", navigation: "sidebar", accent: "#155dfc", font: "Inter", arabicFont: "IBM Plex Sans Arabic" },
] as const;

/** Hides what the development server draws over the page, so baselines hold only the design. */
const screenshotStyle = fileURLToPath(new URL("../support/screenshot.css", import.meta.url));

const viewports = [
  { name: "phone", width: 375, height: 812 },
  { name: "tablet", width: 768, height: 1024 },
  { name: "desktop", width: 1440, height: 900 },
] as const;

async function chooseDesign(tenant: TestTenant, key: string) {
  const api = await tenantApi(tenant);
  const brands = (await (await api.get("/api/v1/experience/brands")).json()) as { id: string; name: string }[];
  const saved = await api.put(`/api/v1/experience/brands/${brands[0]!.id}`, {
    data: { name: brands[0]!.name, tokens: { "design.preset": { $value: key } } },
  });
  expect(saved.ok(), await saved.text()).toBe(true);
  await api.dispose();
}

async function expectNoSeriousViolations(page: Page, testInfo: TestInfo, label: string) {
  await expectAccessible(page, testInfo, label);
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious");
  expect(serious.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(" ")).join(", ")})`), `serious axe violations on ${label}`).toEqual([]);
}

/** Visible controls under `root` that are smaller than the 24 by 24 pixel minimum target (WCAG 2.2 SC 2.5.8). */
function smallTargets(root: Locator) {
  return root.evaluate((el) =>
    [...el.querySelectorAll<HTMLElement>("button, [role=tab], input, select, textarea, a.button")]
      .filter((control) => control.offsetParent !== null)
      .map((control) => ({ html: control.outerHTML.slice(0, 80), box: control.getBoundingClientRect() }))
      .filter(({ box }) => box.width < 24 || box.height < 24)
      .map(({ html }) => html),
  );
}

async function screenshotForReview(page: Page, name: string) {
  const dir = process.env.DESIGN_SCREENSHOTS_DIR;
  if (!dir) return;
  mkdirSync(dir, { recursive: true });
  // The development server's own badge is not part of the design.
  await page.evaluate(() => document.querySelector("nextjs-portal")?.remove());
  await page.screenshot({ path: `${dir}/${name}.png`, fullPage: true });
}

for (const design of designs) {
  for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
    test.describe(`${design.key} design in ${locale}`, () => {
      test.describe.configure({ mode: "serial" });
      test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      let tenant: TestTenant;

      test.beforeAll(async () => {
        tenant = await createTenant(`ds-${design.key}-${locale}`, locale, "workplace_plus");
        await seedHome(tenant);
        await chooseDesign(tenant, design.key);
      });

      test("the home takes the design, is accessible and fits every viewport", async ({ page }, testInfo) => {
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        await page.goto(`${hostOrigin(tenant.key)}/`);
        const html = page.locator("html");
        await expect(html).toHaveAttribute("data-design", design.key);
        await expect(html).toHaveAttribute("data-nav", design.navigation);
        await expect(html).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
        const styles = await page.evaluate(() => {
          const root = getComputedStyle(document.documentElement);
          return { accent: root.getPropertyValue("--tb-color-accent-base").trim(), font: getComputedStyle(document.body).fontFamily };
        });
        expect(styles.accent).toBe(design.accent);
        expect(styles.font).toContain(locale === "ar" ? design.arabicFont : design.font);
        await expectNoSeriousViolations(page, testInfo, `home-${design.key}-${locale}`);

        for (const viewport of viewports) {
          await page.setViewportSize({ width: viewport.width, height: viewport.height });
          const overflow = await page.evaluate(() => {
            const width = document.documentElement.clientWidth;
            const wide = [...document.querySelectorAll<HTMLElement>("body *")]
              .filter((el) => {
                const box = el.getBoundingClientRect();
                return box.right > width + 1 || box.left < -1;
              })
              .slice(0, 5)
              .map((el) => `${el.tagName.toLowerCase()}.${el.className}`);
            return { by: document.documentElement.scrollWidth - width, wide };
          });
          expect(overflow.by, `horizontal scrolling at ${viewport.name} width: ${overflow.wide.join(", ")}`).toBeLessThanOrEqual(1);
          await screenshotForReview(page, `home-${design.key}-${locale}-${viewport.name}`);
        }
      });

      test("the component gallery is accessible, has large enough targets and matches its baseline", async ({ page }, testInfo) => {
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        await page.setViewportSize({ width: 1280, height: 900 });
        await page.goto(`${hostOrigin(tenant.key)}/ui-gallery`);
        const gallery = page.getByTestId("design-system");
        await expect(gallery).toBeVisible();
        await page.evaluate(() => document.fonts.ready);
        await expectNoSeriousViolations(page, testInfo, `gallery-${design.key}-${locale}`);

        expect(await smallTargets(gallery), "controls smaller than 24 by 24 pixels").toEqual([]);

        for (const id of ["ds-actions", "ds-feedback", "ds-cards"]) {
          // Soft, so one run reports every section that moved.
          await expect.soft(page.locator(`section:has(> h2#${id})`)).toHaveScreenshot(`${id}-${design.key}-${locale}.png`, {
            animations: "disabled",
            caret: "hide",
            stylePath: screenshotStyle,
            maxDiffPixelRatio: 0.02,
          });
        }
        await screenshotForReview(page, `gallery-${design.key}-${locale}`);
      });

      test("the extended component gallery is accessible, has large enough targets and matches its baseline", async ({ page }, testInfo) => {
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        await page.setViewportSize({ width: 1280, height: 900 });
        await page.goto(`${hostOrigin(tenant.key)}/ui-gallery/components`);
        const gallery = page.getByTestId("components-gallery");
        await expect(gallery).toBeVisible();
        await page.evaluate(() => document.fonts.ready);
        await expectNoSeriousViolations(page, testInfo, `components-${design.key}-${locale}`);
        expect(await smallTargets(gallery), "controls smaller than 24 by 24 pixels").toEqual([]);

        for (const id of ["cg-actions", "cg-selection", "cg-status", "cg-structure", "cg-overlays", "cg-menus"]) {
          await expect.soft(page.locator(`section:has(> h2#${id})`)).toHaveScreenshot(`${id}-${design.key}-${locale}.png`, {
            animations: "disabled",
            caret: "hide",
            stylePath: screenshotStyle,
            maxDiffPixelRatio: 0.02,
          });
        }
        await screenshotForReview(page, `components-${design.key}-${locale}`);
      });
    });
  }
}
