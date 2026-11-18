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
  {
    key: "beats", navigation: "rail", accent: "#4338ca", font: "Readex Pro", arabicFont: "Readex Pro", density: "comfortable",
    variants: { news: "editorial", stats: "tinted", rows: "comfortable", tabs: "underline", cards: "raised", briefing: "night" },
  },
  {
    key: "atlas", navigation: "top", accent: "#0b6e4f", font: "IBM Plex Sans", arabicFont: "IBM Plex Sans Arabic", density: "comfortable",
    variants: { news: "compact", stats: "outlined", rows: "comfortable", tabs: "underline", cards: "outlined", briefing: "light" },
  },
  {
    key: "pulse", navigation: "sidebar", accent: "#c2185b", font: "Cairo", arabicFont: "Cairo", density: "comfortable",
    variants: { news: "editorial", stats: "tinted", rows: "comfortable", tabs: "segmented", cards: "raised", briefing: "night" },
  },
  // Inter has no Arabic, so Studio pairs it with IBM Plex Sans Arabic.
  {
    key: "studio", navigation: "sidebar", accent: "#155dfc", font: "Inter", arabicFont: "IBM Plex Sans Arabic", density: "compact",
    variants: { news: "compact", stats: "plain", rows: "dense", tabs: "segmented", cards: "outlined", briefing: "light" },
  },
] as const;

/** Hides what the development server draws over the page, so baselines hold only the design. */
const screenshotStyle = fileURLToPath(new URL("../support/screenshot.css", import.meta.url));

const viewports = [
  { name: "phone", width: 375, height: 812 },
  { name: "tablet", width: 768, height: 1024 },
  { name: "desktop", width: 1440, height: 900 },
] as const;

/** The main pages of spec 036 DLS-118: employee and administration screens, which share one system (DLS-116). */
const mainPages = [
  "/", "/tasks", "/services", "/requests", "/calendar", "/assistant", "/people", "/policies", "/documents", "/kb", "/notifications", "/settings",
  "/admin", "/admin/users", "/admin/plan",
] as const;

/** What the frame promises on every page: no sideways scrolling, no text under 12 px, one title, the shared page header. */
function frameFacts(page: Page) {
  return page.evaluate(() => {
    const width = document.documentElement.clientWidth;
    const small = new Set<string>();
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const el = node.parentElement;
      if (!node.textContent?.trim() || !el || el.closest("nextjs-portal, script, style, noscript")) continue;
      const style = getComputedStyle(el);
      if (style.visibility === "hidden" || el.getClientRects().length === 0) continue;
      if (parseFloat(style.fontSize) < 12) small.add(`${el.tagName.toLowerCase()}.${el.className} ${style.fontSize}`);
    }
    return {
      overflow: document.documentElement.scrollWidth - width,
      small: [...small].slice(0, 5),
      titles: document.querySelectorAll("main h1").length,
      header: document.querySelector("main .ui-page-header") !== null,
      font: getComputedStyle(document.body).fontFamily,
    };
  });
}

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
        // The design's descriptor reaches the page as variant attributes, and its default density applies to a person
        // who has not chosen one (DLS-110).
        for (const [part, variant] of Object.entries(design.variants)) {
          await expect(html).toHaveAttribute(`data-v-${part}`, variant);
        }
        await expect(html).toHaveAttribute("data-density", design.density);
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

      test("the main pages keep the frame, the type floor and the design at every width", async ({ page }, testInfo) => {
        test.slow();
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        for (const path of mainPages) {
          await page.setViewportSize({ width: 1440, height: 900 });
          await page.goto(`${hostOrigin(tenant.key)}${path}`);
          await expect(page.locator("main h1").first()).toBeVisible();
          await expectAccessible(page, testInfo, `page-${design.key}-${locale}${path.replace(/\//g, "-")}`);
          for (const viewport of [viewports[2], viewports[1], { name: "phone", width: 390, height: 844 }]) {
            await page.setViewportSize({ width: viewport.width, height: viewport.height });
            const facts = await frameFacts(page);
            const where = `${path} at ${viewport.name} width`;
            // Soft, so one run reports every page that moved.
            expect.soft(facts.overflow, `horizontal scrolling on ${where}`).toBeLessThanOrEqual(1);
            expect.soft(facts.small, `text under 12 px on ${where}`).toEqual([]);
            expect.soft(facts.titles, `page titles on ${where}`).toBe(1);
            if (path !== "/") expect.soft(facts.header, `shared page header on ${where}`).toBe(true);
            expect.soft(facts.font, `font family on ${where}`).toContain(locale === "ar" ? design.arabicFont : design.font);
          }
        }

        // The other density (DLS-117): the person chooses it, and every main page still fits and keeps the type floor.
        // The design's own density is set back afterwards (a save merges, so an empty one would keep the choice), because
        // the tests that follow expect it.
        const other = design.density === "compact" ? "comfortable" : "compact";
        const api = await tenantApi(tenant);
        try {
          expect((await api.put("/api/v1/me/preference-profile", { data: { values: { density: other } } })).ok()).toBe(true);
          for (const path of mainPages) {
            await page.setViewportSize({ width: 1440, height: 900 });
            await page.goto(`${hostOrigin(tenant.key)}${path}`);
            await expect(page.locator("main h1").first()).toBeVisible();
            await expect(page.locator("html")).toHaveAttribute("data-density", other);
            for (const viewport of [viewports[2], { name: "phone", width: 390, height: 844 }]) {
              await page.setViewportSize({ width: viewport.width, height: viewport.height });
              const facts = await frameFacts(page);
              const where = `${path} at ${viewport.name} width in the ${other} density`;
              expect.soft(facts.overflow, `horizontal scrolling on ${where}`).toBeLessThanOrEqual(1);
              expect.soft(facts.small, `text under 12 px on ${where}`).toEqual([]);
            }
          }
        } finally {
          await api.put("/api/v1/me/preference-profile", { data: { values: { density: design.density } } });
          await api.dispose();
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

        // DLS-111: counts keep whole number ticks, and one data point is a number, not a chart.
        const ticks = await page.getByTestId("cg-chart-counts").locator(".ui-chart-axes g text").allTextContents();
        expect(ticks.length, "value ticks on the counts chart").toBeGreaterThan(1);
        expect(ticks.filter((tick) => /[.,٫]/.test(tick)), "fractional ticks on a chart of counts").toEqual([]);
        const single = page.getByTestId("cg-chart-single");
        await expect(single.locator('[data-chart="single"]')).toBeVisible();
        await expect(single.locator(".ui-chart")).toHaveCount(0);

        // DLS-101: the same summary shows 3 rows in a third width region and 5 in a half width one, then View all.
        for (const [id, rows] of [["cg-budget-third", 3], ["cg-budget-half", 5]] as const) {
          const widget = page.getByTestId(id).locator(".ui-widget");
          await expect(widget).toHaveAttribute("data-budget", String(rows));
          await expect(widget.getByRole("listitem")).toHaveCount(rows);
          await expect(widget.locator("[data-widget-more]")).toContainText(/7|٧/);
        }
        // DLS-110 page cards: outlined draws the hairline on the frame, flat draws neither line nor shadow, in any design.
        const cards = await page.evaluate(() => {
          const root = document.documentElement;
          const before = root.getAttribute("data-v-cards");
          const frame = document.querySelector('[data-testid="cg-budget-third"] .ui-widget')!;
          const read = (variant: string) => {
            root.setAttribute("data-v-cards", variant);
            const style = getComputedStyle(frame);
            return { line: style.borderTopWidth !== "0px" && style.borderTopColor !== "rgba(0, 0, 0, 0)", shadow: style.boxShadow !== "none" };
          };
          const seen = { outlined: read("outlined"), flat: read("flat") };
          if (before === null) root.removeAttribute("data-v-cards");
          else root.setAttribute("data-v-cards", before);
          return seen;
        });
        expect(cards.outlined.line, "outlined page cards draw a hairline").toBe(true);
        expect(cards.flat, "flat page cards draw no line and no shadow").toEqual({ line: false, shadow: false });

        // DLS-103: a placement state sits inside the widget frame.
        await expect(page.getByTestId("cg-budget-state").locator(".ui-widget .ui-state")).toBeVisible();

        for (const id of ["cg-actions", "cg-selection", "cg-status", "cg-structure", "cg-overlays", "cg-menus", "cg-inputs", "cg-dates", "cg-data", "cg-schedule", "cg-panels", "cg-assistant", "cg-agents", "cg-charts", "cg-charts-more", "cg-calendar"]) {
          await expect.soft(page.locator(`section:has(> h2#${id})`)).toHaveScreenshot(`${id}-${design.key}-${locale}.png`, {
            animations: "disabled",
            caret: "hide",
            stylePath: screenshotStyle,
            maxDiffPixelRatio: 0.02,
          });
        }
        await screenshotForReview(page, `components-${design.key}-${locale}`);
      });

      test("the page patterns are accessible, have large enough targets and match their baselines", async ({ page }, testInfo) => {
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        await page.setViewportSize({ width: 1280, height: 900 });
        await page.goto(`${hostOrigin(tenant.key)}/ui-gallery/patterns`);
        const gallery = page.getByTestId("patterns-gallery");
        await expect(gallery).toBeVisible();
        await page.evaluate(() => document.fonts.ready);
        await expectNoSeriousViolations(page, testInfo, `patterns-${design.key}-${locale}`);
        expect(await smallTargets(gallery), "controls smaller than 24 by 24 pixels").toEqual([]);

        for (const id of ["pt-dashboard", "pt-chat", "pt-agent", "pt-record", "pt-board"]) {
          await expect.soft(page.locator(`section:has(> h2#${id})`)).toHaveScreenshot(`${id}-${design.key}-${locale}.png`, {
            animations: "disabled",
            caret: "hide",
            stylePath: screenshotStyle,
            maxDiffPixelRatio: 0.02,
          });
        }
        await screenshotForReview(page, `patterns-${design.key}-${locale}`);
      });
    });
  }
}
