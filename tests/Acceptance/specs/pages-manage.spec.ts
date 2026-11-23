import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, referenceProps, saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 032 R2 T032: the Pages area management screens. A tenant administrator switches a component off, makes a fragment
 * from a page region, adds an approval route, sees the component upgrades and the template import, and restores an earlier
 * draft in the builder. In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`pages area management in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let pageId: string;
    let slug: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`pgm-${locale}`, locale, "enterprise_gov");
      const admin = await tenantApi(tenant);
      slug = `manage-${locale}`;
      const page = await createPage(admin, "T01", slug);
      pageId = page.id;
      const regions = page.draft.regions.map((r) =>
        r.id === "main" ? { ...r, placements: [{ id: "intro", componentRef: "reference.alpha.text@1", props: referenceProps["reference.alpha.text@1"] }] } : r,
      );
      // Saving replaces the first draft, so the builder has an earlier version to restore.
      await saveAndPublish(admin, page, regions);
      await admin.dispose();
    });

    test.beforeEach(async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
    });

    test("switch a component off for the whole organization", async ({ page }, testInfo) => {
      await page.goto(`${hostOrigin(tenant.key)}/admin/pages/controls`);
      const form = page.getByTestId("feature-control-form");
      await expect(form).toBeVisible();
      const subject = form.getByLabel(msg(locale, "builder.manage.controls.subject"));
      // The choice is made again until the form has hydrated and keeps it.
      await expect(async () => {
        await subject.selectOption("reference.alpha.links");
        await expect(subject).toHaveValue("reference.alpha.links", { timeout: 1_000 });
      }).toPass();
      await form.getByRole("button", { name: msg(locale, "builder.manage.controls.save") }).click();
      await expect(page.getByTestId("manage-result")).toHaveText(msg(locale, "builder.manage.controls.saved"));
      const table = page.getByTestId("feature-controls");
      await expect(table.getByText(msg(locale, "builder.manage.controls.off"), { exact: true }).first()).toBeVisible();
      await expectAccessible(page, testInfo, `pages-controls-${locale}`);

      await table.getByRole("button", { name: msg(locale, "builder.manage.controls.history") }).first().click();
      await expect(page.getByTestId("feature-control-history").getByRole("listitem")).toHaveCount(1);
    });

    test("make a fragment from a page region", async ({ page }, testInfo) => {
      await page.goto(`${hostOrigin(tenant.key)}/admin/pages/fragments`);
      const form = page.getByTestId("fragment-form");
      await form.getByLabel(msg(locale, "builder.manage.fragments.key")).fill(`notice-${locale}`);
      await form.getByLabel(msg(locale, "builder.manage.fragments.nameEn")).fill("Safety notice");
      await form.getByLabel(msg(locale, "builder.manage.fragments.nameAr")).fill("تنبيه السلامة");
      await form.getByLabel(msg(locale, "builder.manage.fragments.fromPage")).selectOption(pageId);
      await form.getByLabel(msg(locale, "builder.manage.fragments.fromRegion")).selectOption("main");
      await form.getByRole("button", { name: msg(locale, "builder.manage.fragments.add") }).click();
      await expect(page.getByTestId("manage-result")).toHaveText(msg(locale, "builder.manage.fragments.created"));

      const name = locale === "ar" ? "تنبيه السلامة" : "Safety notice";
      await page.getByRole("button", { name: msg(locale, "builder.manage.fragments.openLabel", { name }) }).click();
      const detail = page.getByTestId("fragment-detail");
      await expect(detail.getByTestId("fragment-versions").getByRole("listitem")).toHaveCount(1);
      await expect(detail.getByTestId("fragment-impact")).not.toHaveText(msg(locale, "builder.manage.fragments.loading"));
      await expectAccessible(page, testInfo, `pages-fragments-${locale}`);
    });

    test("add an approval route for the whole organization", async ({ page }, testInfo) => {
      const admin = await tenantApi(tenant);
      const me = (await (await admin.get("/api/v1/me")).json()) as { id: string };
      const added = await admin.put("/api/v1/experience/page-routes", { data: { orgUnitId: null, approverIds: [me.id] } });
      expect(added.ok()).toBeTruthy();
      await admin.dispose();

      await page.goto(`${hostOrigin(tenant.key)}/admin/pages/routes`);
      const routes = page.getByTestId("approval-routes");
      await expect(routes.getByRole("rowheader", { name: msg(locale, "builder.manage.routes.wholeTenant") })).toBeVisible();
      const whole = msg(locale, "builder.manage.routes.wholeTenant");
      await expect(async () => {
        await routes.getByRole("button", { name: msg(locale, "builder.manage.routes.changeLabel", { scope: whole }) }).click();
        await expect(page.getByRole("heading", { name: msg(locale, "builder.manage.routes.editTitle", { scope: whole }) })).toBeVisible({ timeout: 1_000 });
      }).toPass();
      await expectAccessible(page, testInfo, `pages-routes-${locale}`);

      // Remove the route again so the other tests publish without approval.
      const table = routes.getByRole("table");
      await table.getByRole("button", { name: msg(locale, "builder.manage.routes.remove") }).click();
      await table.getByRole("button", { name: msg(locale, "builder.manage.routes.removeConfirm") }).click();
      await expect(page.getByTestId("manage-result")).toHaveText(msg(locale, "builder.manage.routes.removed"));
    });

    test("see component upgrades and the template import", async ({ page }, testInfo) => {
      await page.goto(`${hostOrigin(tenant.key)}/admin/pages/upgrades`);
      await expect(page.getByTestId("component-upgrades")).toBeVisible();
      await expectAccessible(page, testInfo, `pages-upgrades-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/admin/templates`);
      const form = page.getByTestId("template-import");
      await form.getByLabel(msg(locale, "templates.transfer.id")).fill(`imported-${locale}`);
      await form.getByRole("button", { name: msg(locale, "templates.transfer.import") }).click();
      await expect(form.getByText(msg(locale, "templates.transfer.noFile"))).toBeVisible();
      await expectAccessible(page, testInfo, `templates-import-${locale}`);
    });

    test("release a template after its impact report, then export it", async ({ page }, testInfo) => {
      const id = `notice-board-${locale}`;
      const admin = await tenantApi(tenant);
      const created = await admin.post("/api/v1/experience/templates", { data: { id, basedOn: "T04", names: { en: "Notice board", ar: "لوحة الإعلانات" } } });
      expect(created.ok()).toBeTruthy();
      await admin.dispose();

      await page.goto(`${hostOrigin(tenant.key)}/admin/templates/${id}`);
      await expect(async () => {
        await page.getByRole("button", { name: msg(locale, "templates.release") }).click();
        await expect(page.getByTestId("builder-impact")).toBeVisible({ timeout: 2_000 });
      }).toPass();
      await expectAccessible(page, testInfo, `template-impact-${locale}`);
      await page.getByRole("dialog").getByRole("button", { name: msg(locale, "builder.impact.publish") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "templates.releasedNow") })).toBeVisible();

      const download = page.waitForEvent("download");
      await page.getByTestId("template-export").getByRole("button", { name: msg(locale, "templates.transfer.export") }).click();
      expect((await download).suggestedFilename()).toBe(`${id}.template.json`);
    });

    test("restore an earlier draft in the builder", async ({ page }, testInfo) => {
      await page.goto(`${hostOrigin(tenant.key)}/admin/pages/${pageId}`);
      const revisions = page.getByTestId("draft-revisions");
      await expect(revisions).toBeVisible();
      await revisions.locator("summary").click();
      await revisions.getByRole("button", { name: msg(locale, "builder.revisions.restore") }).first().click();
      await revisions.getByRole("button", { name: msg(locale, "builder.revisions.restoreConfirm") }).click();
      await expect(revisions.getByTestId("manage-result")).toHaveText(msg(locale, "builder.revisions.restored", { revision: "1" }));
      await expectAccessible(page, testInfo, `pages-revisions-${locale}`);

      const admin = await tenantApi(tenant);
      const draft = (await (await admin.get(`/api/v1/experience/pages/${pageId}`)).json()) as { draft: { regions: { id: string; placements: { id: string }[] }[] } };
      expect(draft.draft.regions.find((r) => r.id === "main")?.placements.map((p) => p.id)).not.toContain("intro");
      await admin.dispose();
    });
  });
}
