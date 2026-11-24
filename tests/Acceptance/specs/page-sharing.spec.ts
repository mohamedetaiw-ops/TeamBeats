import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 033 R2 T019 (US4 scenarios 4 and 5): an organization's page reaches its own branch; its publisher shares it with
 * another organization from the page builder, and people there see it with its owner named. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`page sharing in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let pageId: string;
    let slug: string;
    const names = { gulf: { en: "Acme Gulf", ar: "أكمي الخليج" }, africa: { en: "Acme Africa", ar: "أكمي أفريقيا" } };

    test.beforeAll(async () => {
      tenant = await createTenant(`psh-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const unit = async (code: string, n: { en: string; ar: string }) => {
        const created = await api.post("/api/v1/org/units", { data: { parentId: null, typeKey: "department", code, names: n } });
        expect(created.ok(), await created.text()).toBe(true);
        return ((await created.json()) as { unit: { id: string } }).unit.id;
      };
      const gulf = await unit("GLF", names.gulf);
      const africa = await unit("AFR", names.africa);
      // The administrator works in Africa, another branch than the page's owner.
      const me = (await (await api.get("/api/v1/me")).json()) as { id: string };
      expect((await api.post(`/api/v1/org/units/${africa}/memberships`, { data: { userId: me.id, type: "primary" } })).ok()).toBe(true);
      slug = `gulf-safety-${locale}`;
      const created = await api.post("/api/v1/experience/pages", { data: { templateId: "T01", slug, orgUnitId: gulf } });
      expect(created.status(), await created.text()).toBe(201);
      const page = await created.json();
      pageId = page.id;
      const intro = { id: "intro", componentRef: "reference.alpha.text@1", props: { body: { en: "Wear a helmet.", ar: "ارتد الخوذة." } } };
      await saveAndPublish(
        api,
        page,
        page.draft.regions.map((r: { id: string }) => (r.id === "main" ? { ...r, placements: [intro] } : r)),
      );
      await api.dispose();
    });

    test("the publisher shares a page with another organization, which sees its owner", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      // Before the share the page is still reachable to the administrator, who may edit it, and shows no owner label.
      await page.goto(`${hostOrigin(tenant.key)}/p/${slug}`);
      await expect(page.locator(".xp-page")).toBeVisible();
      await expect(page.getByTestId("page-shared-by")).toHaveCount(0);

      await page.goto(`${hostOrigin(tenant.key)}/admin/pages/${pageId}`);
      const sharing = page.getByTestId("page-sharing");
      await expect(sharing).toBeVisible();
      await expect(sharing).toContainText(msg(locale, "builder.sharing.none"));
      // The choice is made again until the panel has hydrated and keeps it.
      await expect(async () => {
        await sharing.getByLabel(msg(locale, "builder.sharing.organization"), { exact: true }).selectOption({ label: names.africa[locale] });
        await expect(sharing.getByRole("button", { name: msg(locale, "builder.sharing.share"), exact: true })).toBeEnabled({ timeout: 1_000 });
      }).toPass();
      await sharing.getByRole("button", { name: msg(locale, "builder.sharing.share"), exact: true }).click();
      await expect(sharing.getByTestId("manage-result")).toHaveText(msg(locale, "builder.sharing.shared", { organization: names.africa[locale] }));
      await expect(sharing.getByRole("listitem")).toHaveCount(1);
      await expectAccessible(page, testInfo, `page-sharing-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/p/${slug}`);
      await expect(page.getByTestId("page-shared-by")).toHaveText(msg(locale, "experience.sharedBy", { owner: names.gulf[locale] }));
      await expectAccessible(page, testInfo, `page-shared-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/admin/pages/${pageId}`);
      await page.getByTestId("page-sharing").getByRole("button", { name: msg(locale, "builder.sharing.removeFor", { organization: names.africa[locale] }) }).click();
      await expect(page.getByTestId("page-sharing")).toContainText(msg(locale, "builder.sharing.none"));
    });
  });
}
