import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, referenceProps, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 032 R2 T025 (US6 scenario 4): a publisher schedules the draft to go live and end at times in the tenant's time
 * zone from the builder, sees what waits, and cancels it. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`page schedule in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let pageId: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`psc-${locale}`, locale, "workplace_plus");
      const admin = await tenantApi(tenant);
      const page = await createPage(admin, "T01", `shifts-${locale}`);
      pageId = page.id;
      const draft = {
        ...page.draft,
        regions: page.draft.regions.map((r) =>
          r.id === "main" ? { ...r, placements: [{ id: "intro", componentRef: "reference.alpha.text@1", props: referenceProps["reference.alpha.text@1"] }] } : r,
        ),
      };
      const saved = await admin.put(`/api/v1/experience/pages/${page.id}/draft`, { data: { definition: draft }, headers: { "if-match": page.eTag } });
      expect(saved.ok(), await saved.text()).toBe(true);
      await admin.dispose();
    });

    test("schedule the draft and cancel it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/pages/${pageId}`);
      const panel = page.getByTestId("builder-schedule");
      await expect(panel).toBeVisible();
      const next = new Date(Date.now() + 3 * 24 * 3600 * 1000);
      const day = next.toISOString().slice(0, 10);
      await panel.getByLabel(m("builder.schedule.goLive")).fill(`${day}T09:00`);
      await panel.getByLabel(m("builder.schedule.ends")).fill(`${day}T18:00`);
      await panel.getByRole("button", { name: m("builder.schedule.schedule") }).click();
      await expect(page.getByTestId("builder-schedule-waiting")).toBeVisible();
      await expectAccessible(page, testInfo, `page-schedule-${locale}`);

      // What waits is kept on the server and shown again after a reload.
      await page.reload();
      await expect(page.getByTestId("builder-schedule-waiting")).toBeVisible();
      await panel.getByRole("button", { name: m("builder.schedule.cancel") }).click();
      await expect(page.getByTestId("builder-schedule-waiting")).toBeHidden();
      await expect(panel.getByLabel(m("builder.schedule.goLive"))).toBeVisible();
    });
  });
}
