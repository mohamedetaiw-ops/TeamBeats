import type { APIRequestContext } from "@playwright/test";
import { expectDetailConformant } from "../support/a11y";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 009 R3 T039 (US10): the administrator shares a site with a guest space from the site's Guests section; the guest
 * signs in, finds the site on the space page and reads its published item, with nothing else of the intranet. In
 * English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`sites shared with guests in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    test.describe.configure({ mode: "serial" });
    let tenant: TestTenant;
    let guest: string;
    let siteId: string;
    let spaceId: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const spaceName = locale === "ar" ? "الموزعون" : "Distributors";
    const siteName = locale === "ar" ? "أخبار الشركاء" : "Partner news";
    const title = locale === "ar" ? "دليل قائمة الأسعار" : "Price list guide";

    async function ok(response: Awaited<ReturnType<APIRequestContext["post"]>>) {
      expect(response.ok(), `${response.url()}: ${await response.text()}`).toBe(true);
      return (await response.json()) as Record<string, unknown>;
    }

    test.beforeAll(async () => {
      tenant = await createTenant(`gsite-${locale}`, locale, "enterprise_gov");
      guest = `rania@partner-${tenant.key}.example`;
      const api = await tenantApi(tenant);
      const version = ((await ok(await api.get("/api/v1/spaces/manage/settings"))) as { version: number }).version;
      await ok(await api.put("/api/v1/spaces/manage/settings", { data: { creationUnits: [], guestsAllowed: true, guestDays: 60, version } }));
      spaceId = (await ok(await api.post("/api/v1/spaces", { data: { name: spaceName, description: spaceName, visibility: "private", template: "project" } }))).id as string;
      await ok(await api.post(`/api/v1/spaces/${spaceId}/guests`, { data: { email: guest, days: 30 } }));
      siteId = (await ok(await api.post("/api/v1/content/sites", { data: { names: { en: "Partner news", ar: "أخبار الشركاء" } } }))).id as string;
      const item = await ok(await api.post("/api/v1/content/items", {
        data: {
          siteId, type: "news", classification: "internal",
          variants: { [locale]: { title, body: { blocks: [{ type: "paragraph", children: [{ text: locale === "ar" ? "الأسعار الجديدة تبدأ الشهر القادم." : "New prices start next month." }] }] } } },
        },
      }));
      await ok(await api.post(`/api/v1/content/items/${item.id as string}/publish`, { data: {} }));
      await api.dispose();
    });

    test("share a site with a guest space from its Guests section", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/sites/${siteId}`);
      const section = page.getByRole("region", { name: m("content.sites.guests.title") });
      await expect(section.getByText(m("content.sites.guests.none"))).toBeVisible();
      await section.getByLabel(m("content.sites.guests.space")).selectOption({ label: spaceName });
      await section.getByRole("button", { name: m("content.sites.guests.share") }).click();
      await expect(section.getByTestId("site-guest-shares")).toContainText(spaceName);
      await expectAccessible(page, testInfo, `site-guests-${locale}`);
    });

    test("the guest reads the shared site in the space", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, guest, locale);
      await page.goto(`${hostOrigin(tenant.key)}/spaces/${spaceId}`);
      const shared = page.getByTestId("shared-sites");
      await shared.getByRole("link", { name: siteName }).click();
      await expect(page.getByRole("heading", { level: 1, name: siteName })).toBeVisible();
      await expectAccessible(page, testInfo, `shared-site-${locale}`);
      await expectDetailConformant(page, testInfo, tenant, "/spaces/[id]/sites/[siteId]");
      await page.getByTestId("shared-site-items").getByRole("link", { name: title }).click();
      await expect(page.getByRole("heading", { level: 1, name: title })).toBeVisible();
      await expect(page.getByTestId("shared-item")).toBeVisible();
      await expectAccessible(page, testInfo, `shared-item-${locale}`);
      await expectDetailConformant(page, testInfo, tenant, "/spaces/[id]/sites/[siteId]/items/[itemId]");
    });
  });
}
