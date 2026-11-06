import type { Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, fillWithReferencePacks, referenceProps, saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 032 renderer (T017): both reference packs on all 12 templates, at the reference widths in English and Arabic
 * with no horizontal scroll, failing, slow and missing components that leave the rest of the page working, declared
 * events between unrelated components, and axe in both languages. Reference packs exist only in the development host.
 */
const widths = [320, 375, 768, 1024, 1440, 1920];
const templates = ["T01", "T02", "T03", "T04", "T05", "T06", "T07", "T08", "T09", "T10", "T11", "T12"];

test.skip(stack.target === "remote", "Reference component packs ship only with the development host.");

type Catalogue = { templates: { id: string; regions: { id: string; policy: { maxPlacements: number } }[] }[] };

const slug = (prefix: string) => `${prefix}-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

async function expectNoHorizontalScroll(page: Page, width: number) {
  await page.setViewportSize({ width, height: 900 });
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, `horizontal overflow at ${width}px`).toBeLessThanOrEqual(0);
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`page renderer in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });

    let tenant: TestTenant;
    const slugs = new Map<string, string>();

    test.beforeAll(async () => {
      tenant = await createTenant(`render-${locale}`, locale);
      const api = await tenantApi(tenant);
      const catalogue = (await (await api.get("/api/v1/experience/catalogue")).json()) as Catalogue;
      for (const template of templates) {
        const page = await createPage(api, template, slug(template.toLowerCase()));
        const max = Object.fromEntries(catalogue.templates.find((t) => t.id === template)!.regions.map((r) => [r.id, r.policy.maxPlacements]));
        await saveAndPublish(api, page, await fillWithReferencePacks(api, page, max));
        slugs.set(template, page.slug);
      }
      await api.dispose();
    });

    for (const template of templates) {
      test(`${template} renders both packs at every width without horizontal scroll`, async ({ page }, testInfo) => {
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        await page.goto(`${hostOrigin(tenant.key)}/p/${slugs.get(template)}`);
        await expect(page.locator("html")).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
        const renderer = page.locator(`[data-template="${template}"]`);
        await expect(renderer).toBeVisible();
        await expect(renderer.locator('[data-component^="reference.alpha."]').first()).toBeAttached();
        await expect(renderer.locator('[data-component^="reference.beta."]').first()).toBeAttached();
        await expect(renderer.locator("[data-placement-state]")).toHaveCount(0);
        for (const width of widths) {
          await expectNoHorizontalScroll(page, width);
        }
        if (template === "T04" || template === "T09") {
          await page.setViewportSize({ width: 1440, height: 900 });
          await expectAccessible(page, testInfo, `page-${template}-${locale}`);
        }
      });
    }
  });
}

test.describe("page renderer faults and events", () => {
  test.use({ locale: "en-US" });

  test("a throwing, slow and missing component leaves the rest of the page working", async ({ page }) => {
    const tenant = await createTenant("faults", "en");
    const api = await tenantApi(tenant);
    const created = await createPage(api, "T01", slug("faults"));
    await saveAndPublish(api, created, [
      {
        ...created.draft.regions[0]!,
        placements: [
          { id: "text", componentRef: "reference.alpha.text@1", props: referenceProps["reference.alpha.text@1"] },
          { id: "throws", componentRef: "reference.alpha.fault@1", props: { mode: "throw" } },
          { id: "slow", componentRef: "reference.alpha.fault@1", props: { mode: "slow" } },
          { id: "missing", componentRef: "reference.beta.missing@1" },
          { id: "after", componentRef: "reference.alpha.kpi@1", props: referenceProps["reference.alpha.kpi@1"] },
        ],
      },
    ]);
    await api.dispose();

    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.goto(`${hostOrigin(tenant.key)}/p/${created.slug}`, { waitUntil: "commit" });
    const placement = (id: string) => page.locator(`.xp-placement[data-placement-id="${id}"]`);
    await expect(placement("text").getByText("Wear a helmet on the plant floor.")).toBeVisible();
    await expect(placement("after").locator("data")).toBeVisible();
    await expect(placement("throws").getByText(msg("en", "experience.placement.failed"))).toBeVisible();
    await expect(placement("missing").getByText(msg("en", "experience.placement.unavailable"))).toBeVisible();
    await expect(placement("slow").locator('[data-fault="slow-done"]')).toBeAttached({ timeout: 15_000 });
  });

  test("components that do not know each other talk only through declared events", async ({ page }) => {
    const tenant = await createTenant("events", "en");
    const api = await tenantApi(tenant);
    const created = await createPage(api, "T01", slug("events"));
    await saveAndPublish(api, created, [
      {
        ...created.draft.regions[0]!,
        placements: [
          { id: "filter", componentRef: "reference.alpha.filter@1", props: {} },
          { id: "list", componentRef: "reference.beta.list@1", props: { pageSize: 5 } },
        ],
      },
    ]);
    await api.dispose();

    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.goto(`${hostOrigin(tenant.key)}/p/${created.slug}`);
    // Both placements hydrated, so the filter's handler and the list's subscription are in place.
    await expect(page.locator('[data-placement-ready="true"]')).toHaveCount(2);
    await page.getByLabel(msg("en", "experience.reference.filterLabel")).fill("helmets");
    await expect(page.locator('[data-filter-query="helmets"]')).toHaveText(
      msg("en", "experience.reference.listFiltered", { query: "helmets", size: "5" }),
    );
  });

  test("another tenant's page and an unknown page are not found", async ({ page }) => {
    const owner = await createTenant("owner", "en");
    const api = await tenantApi(owner);
    const created = await createPage(api, "T01", slug("private"));
    await saveAndPublish(api, created, [{ ...created.draft.regions[0]!, placements: [] }]);
    await api.dispose();

    const other = await createTenant("other", "en");
    await signInWithDevForm(page, other.key, other.adminEmail, "en");
    expect((await page.goto(`${hostOrigin(other.key)}/p/${created.slug}`))?.status()).toBe(404);
    expect((await page.goto(`${hostOrigin(other.key)}/p/no-such-page`))?.status()).toBe(404);
  });
});
