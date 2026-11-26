import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 029 R3 T013 and T014 (US8, US9): the administrator owns a project space with two dated tasks. On the second
 * task's page they say it waits for the first, see the schedule conflict, log time, and find both tasks on the
 * timeline with the conflict and the time on the owners' totals. Loops, viewer refusal, author rules and the tenant
 * boundary: SpaceScheduleTests. In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`space plan and time in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const design = locale === "ar" ? "تصميم نموذج البيانات" : "Design the data model";
    const migrate = locale === "ar" ? "نقل بيانات العملاء" : "Migrate customer data";
    let tenant: TestTenant;
    let spaceId: string;
    let migrateId: string;
    const day = (offset: number) => new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);

    test.beforeAll(async () => {
      tenant = await createTenant(`spl-${locale}`, locale, "workplace_plus", []);
      const api = await tenantApi(tenant);
      const created = await api.post("/api/v1/spaces", { data: { name: locale === "ar" ? "النظام المالي الجديد" : "New finance system", visibility: "private", template: "team" } });
      if (!created.ok()) throw new Error(`Space failed with ${created.status()}: ${await created.text()}`);
      spaceId = ((await created.json()) as { id: string }).id;
      for (const [title, start, due] of [[design, 0, 10], [migrate, 5, 20]] as const) {
        const task = await api.post(`/api/v1/spaces/${spaceId}/tasks`, { data: { title } });
        if (!task.ok()) throw new Error(`Task failed with ${task.status()}: ${await task.text()}`);
        const detail = (await task.json()) as { task: { id: string; version: number } };
        await api.put(`/api/v1/spaces/${spaceId}/tasks/${detail.task.id}/schedule`, { data: { startOn: day(start), dueOn: day(due), version: detail.task.version } });
        if (title === migrate) migrateId = detail.task.id;
      }
      await api.dispose();
    });

    test("the owner links a blocker, sees the conflict and logs time on the task", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/spaces/${spaceId}/tasks/${migrateId}`);
      const main = page.getByRole("main");
      await expect(main.getByText(m("spaces.plan.noBlockers"))).toBeVisible();
      await expect(async () => {
        const add = main.locator("form", { has: page.getByLabel(m("spaces.plan.addBlockerLabel")) });
        await add.getByLabel(m("spaces.plan.addBlockerLabel")).selectOption({ label: design });
        await add.getByRole("button", { name: m("spaces.plan.addBlocker") }).click();
        await expect(main.getByText(m("spaces.plan.conflictTitle"))).toBeVisible({ timeout: 2000 });
      }).toPass();
      await expect(main.getByRole("link", { name: design })).toBeVisible();

      await main.getByLabel(m("spaces.plan.hours"), { exact: true }).fill("1");
      await main.getByLabel(m("spaces.plan.minutes"), { exact: true }).fill("30");
      await main.getByRole("button", { name: m("spaces.plan.logTime") }).click();
      await expect(main.getByRole("list", { name: m("spaces.plan.entriesLabel") }).getByRole("listitem")).toHaveCount(1);
      await expectAccessible(page, testInfo, `spaces-plan-task-${locale}`);
    });

    test("the timeline shows both tasks with the conflict, and the owner sees the time", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/spaces/${spaceId}`);
      const nav = page.getByRole("navigation", { name: m("spaces.parts.nav.label") });
      await nav.getByRole("link", { name: m("spaces.parts.nav.timeline") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("spaces.timelineTitle"));
      const main = page.getByRole("main");
      await expect(main.getByRole("link", { name: design })).toBeVisible();
      await expect(main.getByRole("link", { name: migrate })).toBeVisible();
      await expect(main.getByText(m("spaces.timeline.conflict"), { exact: false }).first()).toBeVisible();
      await expectAccessible(page, testInfo, `spaces-plan-timeline-${locale}`);

      await nav.getByRole("link", { name: m("spaces.parts.nav.time"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("spaces.timeTitle"));
      await expect(main.getByRole("link", { name: migrate })).toBeVisible();
      await expect(main.getByRole("link", { name: m("spaces.time.export") })).toBeVisible();
      await expectAccessible(page, testInfo, `spaces-plan-time-${locale}`);
    });
  });
}
