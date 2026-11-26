import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, platformPost, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

const DOCUMENT =
  "<!doctype html><html><body><p id='hello'>Kudos</p><script>" +
  "window.addEventListener('message', (e) => { if (e.data && e.data.type === 'tb.context') { document.getElementById('hello').textContent = 'Kudos ' + e.data.context.locale; parent.postMessage({ type: 'tb.height', height: 200 }, '*'); } });" +
  "</script></body></html>";

/**
 * Spec 032 R3 FR-031 in the browser (T037): an administrator sends a component for review, the platform approves it, the
 * administrator turns it on, and a page frames it in a sandbox that receives only the declared context and sets its
 * height. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`tenant components in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);

    test.beforeAll(async () => {
      tenant = await createTenant(`tcmp-${locale}`, locale, "enterprise_gov");
    });

    test("a reviewed component is turned on and framed in a sandbox on a page", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/admin/pages/components`);
      await page.getByLabel(m("builder.manage.tenantComponents.idField")).fill("kudos-wall");
      await page.getByLabel(m("builder.manage.tenantComponents.nameEn")).fill("Kudos wall");
      await page.getByLabel(m("builder.manage.tenantComponents.nameAr")).fill("لوحة الشكر");
      await page.getByLabel(m("builder.manage.tenantComponents.document"), { exact: true }).fill(DOCUMENT);
      await page.getByRole("button", { name: m("builder.manage.tenantComponents.submit") }).click();
      await expect(page.getByTestId("tenant-component-kudos-wall-1")).toContainText(m("builder.manage.tenantComponents.status.in_review", { version: 1 }));
      await expectAccessible(page, testInfo, `tenant-components-${locale}`);

      await platformPost(`/api/v1/platform/component-reviews/${tenant.id}/kudos-wall/1`, { approve: true, note: "Checked by security" });
      await page.reload();
      await page.getByLabel(m("builder.manage.tenantComponents.enabledLabel", { name: locale === "ar" ? "لوحة الشكر" : "Kudos wall" })).click();
      await expect(page.getByTestId("manage-result")).toContainText(m("builder.manage.tenantComponents.changed"));

      const api = await tenantApi(tenant);
      const slug = `kudos-${locale}`;
      const created = await createPage(api, "T01", slug);
      await saveAndPublish(api, created, [
        { id: "main", placements: [{ id: "k", componentRef: "sandbox.tenant_component@1", props: { component: "kudos-wall" } }] },
      ]);
      await api.dispose();

      await page.goto(`${origin}/p/${slug}`);
      const frame = page.getByTestId("sandbox-frame");
      await expect(frame).toHaveAttribute("sandbox", "allow-scripts");
      await expect(page.frameLocator("[data-testid=sandbox-frame]").locator("#hello")).toHaveText(`Kudos ${locale}`);
      await expect(frame).toHaveCSS("block-size", "200px");
    });
  });
}
