import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 029 slices 1 and 2: the administrator creates a private project space from the standard template, adds Layla
 * as a member, adds a task, gives it to her with a due date and moves another task to Done from the keyboard menu;
 * Layla finds the task under My tasks in the task inbox and completes it there. Visibility, roles, viewer refusal,
 * milestones, status, reminders and the tenant boundary are covered by SpacesTests and SpaceTaskTests. In English and
 * Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`spaces in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);
    const space = locale === "ar" ? "إطلاق النظام المالي" : "Finance system rollout";
    const task = locale === "ar" ? "مراجعة خطوات الشراء" : "Review the purchase steps";

    test.beforeAll(async () => {
      tenant = await createTenant(`spc-${locale}`, locale, "workplace_plus", ["layla"]);
    });

    test("a colleague opens the empty space directory", async ({ page }, testInfo) => {
      // Signing in also gives Layla her profile, so the administrator can find her in the people directory next.
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/spaces`);
      await expect(page.getByText(m("spaces.directory.emptyTitle"))).toBeVisible();
      await expectAccessible(page, testInfo, `spaces-empty-${locale}`);
    });

    test("an administrator creates a space, adds a member and tracks work on the board", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/spaces`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("spaces.pageTitle"));
      await expectAccessible(page, testInfo, `spaces-directory-${locale}`);
      await page.getByRole("link", { name: m("spaces.create.pageTitle") }).click();

      const main = page.getByRole("main");
      await main.getByLabel(m("spaces.create.name")).fill(space);
      await main.getByLabel(m("spaces.create.visibilityOption.private")).check();
      await expectAccessible(page, testInfo, `spaces-create-${locale}`);
      await main.getByRole("button", { name: m("spaces.create.submit") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(space);
      await expectAccessible(page, testInfo, `spaces-overview-${locale}`);

      await page.getByRole("navigation", { name: m("spaces.parts.nav.label") }).getByRole("link", { name: m("spaces.parts.nav.members") }).click();
      await main.getByLabel(m("spaces.members.search")).fill("layla");
      await main.getByRole("button", { name: m("spaces.members.find") }).click();
      await main.getByRole("list", { name: m("spaces.members.results") }).getByRole("button", { name: m("spaces.members.add") }).first().click();
      await expect(main.getByRole("list", { name: m("spaces.members.listLabel") }).getByRole("listitem")).toHaveCount(2);
      await expectAccessible(page, testInfo, `spaces-members-${locale}`);

      await page.getByRole("navigation", { name: m("spaces.parts.nav.label") }).getByRole("link", { name: m("spaces.parts.nav.tasks") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("spaces.tasksTitle"));
      // The list's name comes from the template in the organization's language, so the field is found by the start of its label.
      const newTaskLabel = m("spaces.board.newTask").split("{list}")[0]!.trim();
      await main.getByLabel(new RegExp(`^${newTaskLabel}`)).fill(task);
      await main.getByRole("button", { name: m("spaces.board.add") }).click();
      await expect(main.getByRole("link", { name: task })).toBeVisible();
      await expectAccessible(page, testInfo, `spaces-board-${locale}`);

      // The keyboard alternative to dragging: the task's "Move to" menu.
      await main.getByRole("button", { name: m("spaces.board.moveTo", { title: task }) }).click();
      await page.getByRole("menuitemradio").last().click();
      await main.getByRole("radiogroup", { name: m("spaces.board.viewLabel") }).getByRole("radio", { name: m("spaces.board.listView") }).check();
      await expect(main.getByLabel(m("spaces.board.completeTask", { title: task }))).toBeChecked();
      await main.getByLabel(m("spaces.board.completeTask", { title: task })).uncheck();
      await expect(main.getByLabel(m("spaces.board.completeTask", { title: task }))).not.toBeChecked();

      // Give the task to Layla with a due date.
      await main.getByRole("link", { name: task }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(task);
      const due = new Date(Date.now() + 5 * 86_400_000).toISOString().slice(0, 10);
      await main.getByLabel(m("spaces.task.dueOn")).fill(due);
      await main.getByRole("group", { name: m("spaces.task.assignees") }).getByRole("checkbox").last().check();
      await main.getByLabel(m("spaces.task.newItem")).fill(locale === "ar" ? "طلبات الشراء" : "Purchase requests");
      await main.getByRole("button", { name: m("spaces.task.addItem") }).click();
      await expect(main.getByRole("list", { name: m("spaces.task.checklistTitle") }).getByRole("listitem")).toHaveCount(1);
      await main.getByRole("button", { name: m("spaces.task.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("spaces.task.saved") })).toBeVisible();
      await expectAccessible(page, testInfo, `spaces-task-${locale}`);
    });

    test("the member sees the task under My tasks and completes it there", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/tasks`);
      await page.getByRole("link", { name: m("services.inbox.myTasksTab") }).click();
      const main = page.getByRole("main");
      await expect(main.getByRole("link", { name: task })).toBeVisible();
      await expect(main.getByRole("link", { name: space })).toBeVisible();
      await expectAccessible(page, testInfo, `spaces-my-tasks-${locale}`);
      await main.getByLabel(m("spaces.mine.complete", { title: task })).check();
      await expect(main.getByLabel(m("spaces.mine.complete", { title: task }))).toBeChecked();
    });
  });
}
