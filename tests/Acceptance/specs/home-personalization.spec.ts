import type { Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, referenceProps, saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { stack } from "../support/stack";

/**
 * Spec 008 T012 and T013 with part of T015: the employee home from the home template, personalization mode by keyboard
 * only (move, hide, add), live announcements, undo in reverse order, the layout surviving a reload, and reset with
 * confirmation, in English and Arabic with axe. The protected top area offers no tools.
 */
test.skip(stack.target === "remote", "Pages use the reference component packs of the development host.");

const unique = (prefix: string) => `${prefix}-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

async function seedHome(tenant: TestTenant) {
  const api = await tenantApi(tenant);
  const page = await createPage(api, "H01", unique("home"));
  await saveAndPublish(api, page, page.draft.regions.map((r) => ({
    ...r,
    placements: r.id === "hero"
      ? [{ id: "critical", componentRef: "reference.alpha.text@1", props: referenceProps["reference.alpha.text@1"] }]
      : [
          { id: "services", componentRef: "reference.alpha.links@1", props: referenceProps["reference.alpha.links@1"] },
          { id: "numbers", componentRef: "reference.alpha.kpi@1", props: referenceProps["reference.alpha.kpi@1"] },
        ],
  })));
  const shell = await api.put("/api/v1/experience/shell/definition", {
    data: { start: ["identity", "global_navigation"], center: ["search"], end: ["settings"], below: ["breadcrumbs"], utilities: [], homePageId: page.id },
  });
  expect(shell.ok(), await shell.text()).toBe(true);
  await api.dispose();
}

const order = (page: Page, region: string) =>
  page.locator(`[data-region="${region}"] .xp-placement`).evaluateAll((els) => els.map((e) => e.getAttribute("data-placement-id")));

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`home personalization in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const n = (x: number) => new Intl.NumberFormat(locale).format(x);
    const item = (position: number, count: number, region: number) =>
      msg(locale, "personalization.item", { position: n(position), count: n(count), region: n(region) });

    test.beforeAll(async () => {
      tenant = await createTenant(`home-${locale}`, locale);
      await seedHome(tenant);
    });

    test("an employee personalizes the home by keyboard, undoes in order and resets", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await expect(page.locator("main")).toContainText(locale === "en" ? "Wear a helmet on the plant floor." : "ارتد الخوذة في أرض المصنع.");
      // The calendar card is the suggested personal item of every default home (spec 008 FR-051).
      await expect.poll(() => order(page, "grid")).toEqual(["home-calendar-overview", "services", "numbers"]);

      const toggle = page.getByRole("button", { name: msg(locale, "personalization.start") });
      await toggle.focus();
      await page.keyboard.press("Enter");
      const status = page.locator("main [role=status]").first();
      await expect(status).toHaveText(msg(locale, "personalization.modeOn"));
      // The protected top area has no tools; the personal area has its add button and tools on each item.
      await expect(page.getByRole("group", { name: item(1, 1, 1) })).toHaveCount(0);
      await expect(page.getByRole("button", { name: msg(locale, "personalization.add", { region: n(2) }) })).toBeVisible();
      await expectAccessible(page, testInfo, `home-personalization-${locale}`);

      // 0. The suggested calendar card is the user's own, so it is removed like any personal item, and stays removed.
      await page.getByRole("group", { name: item(1, 3, 2) }).getByRole("button", { name: msg(locale, "personalization.remove") }).click();
      await expect.poll(() => order(page, "grid")).toEqual(["services", "numbers"]);

      // 1. Move the second item up, by keyboard. Focus follows the item.
      await page.getByRole("group", { name: item(2, 2, 2) }).getByRole("button", { name: msg(locale, "personalization.moveUp") }).focus();
      await page.keyboard.press("Enter");
      await expect.poll(() => order(page, "grid")).toEqual(["numbers", "services"]);
      await expect(status).toHaveText(msg(locale, "personalization.moved", { position: n(1), count: n(2), region: n(2) }));
      await expect(page.getByRole("group", { name: item(1, 2, 2) }).getByRole("button", { name: msg(locale, "personalization.moveDown") })).toBeFocused();

      // 2. Hide the page's item that is now second.
      await page.getByRole("group", { name: item(2, 2, 2) }).getByRole("button", { name: msg(locale, "personalization.hide") }).focus();
      await page.keyboard.press("Enter");
      await expect.poll(() => order(page, "grid")).toEqual(["numbers"]);

      // 3. Add a component from the add panel; it is the user's own.
      await page.getByRole("button", { name: msg(locale, "personalization.add", { region: n(2) }) }).focus();
      await page.keyboard.press("Enter");
      const listName = locale === "en" ? "Item list (reference)" : "قائمة عناصر (مرجعية)";
      const panel = page.getByRole("region", { name: msg(locale, "personalization.addPanel", { region: n(2) }) });
      await panel.getByRole("button", { name: msg(locale, "personalization.addItem", { name: listName }) }).focus();
      await page.keyboard.press("Enter");
      await expect.poll(async () => (await order(page, "grid")).length).toBe(2);
      await expect(page.locator('[data-region="grid"] .xp-placement').first()).toHaveAttribute("data-personal", "true");

      // The layout survives a reload.
      await page.reload();
      await expect.poll(async () => (await order(page, "grid")).slice(1)).toEqual(["numbers"]);
      await page.getByRole("button", { name: msg(locale, "personalization.start") }).click();

      // A fresh page session has no undo history, so make three changes and undo them in reverse order.
      await page.getByRole("group", { name: item(2, 2, 2) }).getByRole("button", { name: msg(locale, "personalization.moveUp") }).click();
      await expect.poll(async () => (await order(page, "grid"))[0]).toBe("numbers");
      await page.getByRole("group", { name: item(2, 2, 2) }).getByRole("button", { name: msg(locale, "personalization.remove") }).click();
      await expect.poll(() => order(page, "grid")).toEqual(["numbers"]);
      await page.getByRole("group", { name: item(1, 1, 2) }).getByRole("button", { name: msg(locale, "personalization.hide") }).click();
      await expect.poll(() => order(page, "grid")).toEqual([]);

      const undo = page.getByRole("button", { name: msg(locale, "personalization.undo") });
      await undo.click();
      await expect.poll(() => order(page, "grid")).toEqual(["numbers"]);
      await undo.click();
      await expect.poll(async () => (await order(page, "grid"))[0]).toBe("numbers");
      await expect.poll(async () => (await order(page, "grid")).length).toBe(2);
      await undo.click();
      await expect.poll(async () => (await order(page, "grid"))[1]).toBe("numbers");
      await expect(status).toHaveText(msg(locale, "personalization.undone"));
      await expect(undo).toBeDisabled();

      // Reset asks first, then returns the organization's layout.
      await page.getByRole("button", { name: msg(locale, "personalization.reset") }).click();
      const question = page.getByRole("alertdialog", { name: msg(locale, "personalization.reset") });
      await expect(question.getByRole("button", { name: msg(locale, "personalization.cancel") })).toBeFocused();
      await question.getByRole("button", { name: msg(locale, "personalization.resetConfirm") }).click();
      await expect.poll(() => order(page, "grid")).toEqual(["home-calendar-overview", "services", "numbers"]);
      await expect(status).toHaveText(msg(locale, "personalization.resetDone"));

      // Leaving the mode is announced and returns focus to the mode button.
      await page.getByRole("button", { name: msg(locale, "personalization.done") }).click();
      await expect(status).toHaveText(msg(locale, "personalization.modeOff"));
      await expect(page.getByRole("button", { name: msg(locale, "personalization.start") })).toBeFocused();
      await expect(page.getByRole("button", { name: msg(locale, "personalization.add", { region: n(2) }) })).toHaveCount(0);

      // On a phone the mode and its tools fit the screen.
      await page.setViewportSize({ width: 375, height: 800 });
      await page.getByRole("button", { name: msg(locale, "personalization.start") }).click();
      await page.getByRole("button", { name: msg(locale, "personalization.add", { region: n(2) }) }).click();
      await expect(page.getByRole("region", { name: msg(locale, "personalization.addPanel", { region: n(2) }) })).toBeVisible();
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);
    });

    test("an employee drags an item to a new place, and the move survives a reload (spec 008 FR-061)", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await expect.poll(() => order(page, "grid")).toEqual(["home-calendar-overview", "services", "numbers"]);
      await page.getByRole("button", { name: msg(locale, "personalization.start") }).click();
      await page.getByRole("group", { name: item(1, 3, 2) }).getByRole("button", { name: msg(locale, "personalization.remove") }).click();
      await expect.poll(() => order(page, "grid")).toEqual(["services", "numbers"]);

      const handle = page.getByRole("group", { name: item(2, 2, 2) }).getByRole("button", { name: msg(locale, "personalization.drag") });
      const target = page.locator('[data-placement-id="services"]');
      await target.scrollIntoViewIfNeeded();
      const from = (await handle.boundingBox())!;
      const to = (await target.boundingBox())!;
      await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
      await page.mouse.down();
      await page.mouse.move(to.x + to.width / 2, to.y + 8, { steps: 8 });
      await expect(target).toHaveAttribute("data-drop", "before");
      await page.mouse.up();
      await expect.poll(() => order(page, "grid")).toEqual(["numbers", "services"]);
      await expect(page.locator("main [role=status]").first()).toHaveText(msg(locale, "personalization.moved", { position: n(1), count: n(2), region: n(2) }));

      await page.reload();
      await expect.poll(() => order(page, "grid")).toEqual(["numbers", "services"]);

      await page.getByRole("button", { name: msg(locale, "personalization.start") }).click();
      await page.getByRole("button", { name: msg(locale, "personalization.reset") }).click();
      await page.getByRole("alertdialog", { name: msg(locale, "personalization.reset") }).getByRole("button", { name: msg(locale, "personalization.resetConfirm") }).click();
      await expect.poll(() => order(page, "grid")).toEqual(["home-calendar-overview", "services", "numbers"]);
    });
  });
}
