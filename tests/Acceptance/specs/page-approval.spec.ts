import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, referenceProps, tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 032 R2 T023 (US6 scenario 1): where an approval route covers a page, the builder submits it for approval instead
 * of publishing, and the approver sees what changes and approves it from the inbox task, which puts it live. In English
 * and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`page approval in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let pageId: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`pga-${locale}`, locale, "enterprise_gov", ["rana"]);
      const admin = await tenantApi(tenant);
      const rana = await tenantApiAs(tenant, `rana@${tenant.key}.example`);
      const ranaId = ((await (await rana.get("/api/v1/me")).json()) as { id: string }).id;
      const route = await admin.put("/api/v1/experience/page-routes", { data: { orgUnitId: null, approverIds: [ranaId] } });
      expect(route.ok(), await route.text()).toBe(true);
      const page = await createPage(admin, "T01", `safety-${locale}`);
      pageId = page.id;
      const draft = {
        ...page.draft,
        regions: page.draft.regions.map((r) =>
          r.id === "main" ? { ...r, placements: [{ id: "intro", componentRef: "reference.alpha.text@1", props: referenceProps["reference.alpha.text@1"] }] } : r,
        ),
      };
      const saved = await admin.put(`/api/v1/experience/pages/${page.id}/draft`, { data: { definition: draft }, headers: { "if-match": page.eTag } });
      expect(saved.ok(), await saved.text()).toBe(true);
      await rana.dispose();
      await admin.dispose();
    });

    test("submit a covered page and approve it from the inbox", async ({ page, browser }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/admin/pages/${pageId}`);
      await expect(page.getByText(m("builder.editor.approval.required"))).toBeVisible();
      // The impact report comes first (T024): cancel keeps the page as it is, confirming submits.
      const submit = page.getByRole("button", { name: m("builder.editor.approval.submit") });
      await submit.click();
      const dialog = page.getByRole("dialog");
      await expect(dialog.getByTestId("builder-impact")).toContainText(m("builder.impact.tenantWide"));
      await expectAccessible(page, testInfo, `page-impact-${locale}`);
      await dialog.getByRole("button", { name: m("builder.impact.cancel") }).first().click();
      await expect(dialog).toBeHidden();
      await expect(page.getByTestId("builder-pending")).toBeHidden();
      await submit.click();
      await dialog.getByRole("button", { name: m("builder.impact.submit") }).click();
      await expect(page.getByTestId("builder-pending")).toBeVisible();
      await expectAccessible(page, testInfo, `page-approval-pending-${locale}`);

      const rana = await tenantApiAs(tenant, `rana@${tenant.key}.example`);
      const tasks = (await (await rana.get("/api/v1/tasks")).json()) as { source: string; taskId: string; link: string }[];
      const task = tasks.find((x) => x.source === "page_approval");
      expect(task).toBeDefined();
      await rana.dispose();

      const context = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const approver = await context.newPage();
      await signInWithDevForm(approver, tenant.key, `rana@${tenant.key}.example`, locale);
      await approver.goto(`${origin}${task!.link}`);
      const changes = approver.getByTestId("approval-changes");
      await expect(changes).toContainText(m("builder.approval.change.added"));
      await expect(approver.getByTestId("approval-preview-frame")).toBeVisible();
      await expectAccessible(approver, testInfo, `page-approval-decide-${locale}`);
      await approver.getByRole("button", { name: m("services.decision.approve") }).click();
      await expect(approver.getByTestId("approval-facts")).toContainText(m("builder.approval.statuses.approved"));
      await context.close();

      await page.reload();
      await expect(page.getByTestId("builder-pending")).toBeHidden();
      await expect(page.getByText(m("builder.pages.published", { release: "1" }))).toBeVisible();
    });
  });
}
