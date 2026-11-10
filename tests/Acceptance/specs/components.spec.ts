import type { Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 036 DLS-031 to DLS-038: the actions, selection, status, structure, overlay, menu, input and date components
 * of the extended catalogue.
 * Each one is driven with the keyboard, in English and in Arabic, and checked for the name, role and state that
 * assistive technology reads. The gallery exists only with the development fixtures.
 */
test.skip(stack.target === "remote", "The component gallery ships only with the development fixtures.");

async function openGallery(page: Page, tenant: TestTenant, locale: LaunchLocale) {
  await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
  await page.goto(`${hostOrigin(tenant.key)}/ui-gallery/components`);
  await expect(page.getByRole("heading", { level: 1, name: msg(locale, "components_gallery.title") })).toBeVisible();
  // Interactive once hydrated.
  await expect(page.locator('[data-testid="components-gallery"][data-hydrated="true"]')).toBeAttached();
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  const t = (key: string, values: Record<string, string> = {}) => msg(locale, `components_gallery.${key}`, values);
  const forward = locale === "ar" ? "ArrowLeft" : "ArrowRight";
  const back = locale === "ar" ? "ArrowRight" : "ArrowLeft";

  test.describe(`extended components in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`cg-${locale}`, locale);
    });

    test("buttons, icon buttons and groups have names, sizes and pressed state", async ({ page }, testInfo) => {
      await openGallery(page, tenant, locale);
      const actions = page.locator("section:has(> h2#cg-actions)");

      // An icon only button is named by its label; the picture is hidden from assistive technology.
      const edit = actions.getByRole("button", { name: t("actions.edit"), exact: true });
      await expect(edit).toBeVisible();
      await expect(edit.locator("svg")).toHaveAttribute("aria-hidden", "true");

      // Every size keeps the 24 pixel minimum target; the medium size keeps the 44 pixel one.
      for (const button of await actions.locator("button, a.button").all()) {
        const box = (await button.boundingBox())!;
        const floor = (await button.getAttribute("data-size")) === null ? 44 : 24;
        expect(Math.min(box.width, box.height), await button.innerText()).toBeGreaterThanOrEqual(floor);
      }

      // A button group is one named group; the chosen view is pressed and Space changes it.
      const group = actions.getByRole("group", { name: t("actions.group") });
      const week = group.getByRole("button", { name: t("actions.week") });
      const month = group.getByRole("button", { name: t("actions.month") });
      await expect(week).toHaveAttribute("aria-pressed", "true");
      await month.focus();
      await page.keyboard.press("Space");
      await expect(month).toHaveAttribute("aria-pressed", "true");
      await expect(week).toHaveAttribute("aria-pressed", "false");

      // A link that looks like a button stays a link.
      await expect(actions.getByRole("link", { name: t("actions.open") })).toHaveClass(/button/);
      await expectAccessible(page, testInfo, `components-actions-${locale}`);
    });

    test("checkboxes, switches, radio cards, the segmented control and the slider work from the keyboard", async ({ page }) => {
      await openGallery(page, tenant, locale);
      const selection = page.locator("section:has(> h2#cg-selection)");

      // Checkbox: Space toggles; the hint and the error are tied to the box; "select all" reports mixed.
      const notify = selection.getByRole("checkbox", { name: t("selection.notify") });
      await expect(notify).toBeChecked();
      await expect(notify).toHaveAccessibleDescription(t("selection.notifyHint"));
      await notify.focus();
      await page.keyboard.press("Space");
      await expect(notify).not.toBeChecked();
      const terms = selection.getByRole("checkbox", { name: t("selection.terms") });
      await expect(terms).toHaveAttribute("aria-invalid", "true");
      await expect(terms).toHaveAccessibleDescription(t("selection.termsError"));
      const all = selection.getByRole("checkbox", { name: t("selection.all") });
      expect(await all.evaluate((el: HTMLInputElement) => el.indeterminate)).toBe(true);
      await all.focus();
      await page.keyboard.press("Space");
      await expect(all).toBeChecked();
      expect(await all.evaluate((el: HTMLInputElement) => el.indeterminate)).toBe(false);
      await expect(selection.getByRole("checkbox", { name: t("selection.disabled") })).toBeDisabled();

      // Switch: a switch role with an on or off state; Space flips it, and the whole card is the target.
      const digest = selection.getByRole("switch", { name: t("selection.digest") });
      await expect(digest).not.toBeChecked();
      await digest.focus();
      await page.keyboard.press("Space");
      await expect(digest).toBeChecked();
      const focus = selection.getByRole("switch", { name: t("selection.focus") });
      await expect(focus).toBeChecked();
      // The label is stretched over the whole card, so a click on the hint lands on it.
      await selection.getByText(t("selection.focusHint")).click({ force: true });
      await expect(focus).not.toBeChecked();

      // Radio cards: one named group; arrow keys move the choice; clicking the hint picks the card.
      const plan = selection.getByRole("group", { name: t("selection.plan") });
      const hybrid = plan.getByRole("radio", { name: t("selection.hybrid") });
      await expect(hybrid).toBeChecked();
      await hybrid.focus();
      await page.keyboard.press("ArrowDown");
      await expect(plan.getByRole("radio", { name: t("selection.remote") })).toBeChecked();
      await plan.getByText(t("selection.officeHint")).click({ force: true });
      await expect(plan.getByRole("radio", { name: t("selection.office") })).toBeChecked();

      // Segmented control: a radio group whose arrow keys follow the reading direction, and whose thumb follows.
      const period = selection.getByRole("radiogroup", { name: t("selection.view") });
      const weekly = period.getByRole("radio", { name: t("selection.weekly") });
      await expect(weekly).toBeChecked();
      await weekly.focus();
      await page.keyboard.press(forward);
      const monthly = period.getByRole("radio", { name: t("selection.monthly") });
      await expect(monthly).toBeChecked();
      await expect(monthly).toBeFocused();
      await expect
        .poll(async () => {
          const thumb = (await period.locator(".ui-segmented-thumb").boundingBox())!;
          const segment = (await period.locator(".ui-segment:has(input:checked)").boundingBox())!;
          const center = thumb.x + thumb.width / 2;
          return center > segment.x && center < segment.x + segment.width;
        })
        .toBe(true);
      await page.keyboard.press(back);
      await expect(weekly).toBeChecked();

      // Slider: arrow keys step by the step; the value is announced with its unit and shown as text.
      const size = selection.getByRole("slider", { name: t("selection.textSize") });
      await expect(size).toHaveAttribute("aria-valuetext", t("selection.textSizeValue", { value: "125" }));
      await size.focus();
      await page.keyboard.press("ArrowUp");
      await expect(size).toHaveValue("150");
      await expect(size).toHaveAttribute("aria-valuetext", t("selection.textSizeValue", { value: "150" }));
      await expect(selection.locator(".ui-slider output")).toHaveText(t("selection.textSizeValue", { value: "150" }));
      await page.keyboard.press("End");
      await expect(size).toHaveValue("200");
      await page.keyboard.press("Home");
      await expect(size).toHaveValue("100");
    });

    test("chips toggle and remove, and an empty tag group says so", async ({ page }) => {
      await openGallery(page, tenant, locale);
      const selection = page.locator("section:has(> h2#cg-selection)");

      // A selectable chip is a toggle button.
      const topics = selection.getByRole("group", { name: t("selection.topics") });
      const events = topics.getByRole("button", { name: t("selection.topicEvents") });
      await expect(events).toHaveAttribute("aria-pressed", "false");
      await events.focus();
      await page.keyboard.press("Enter");
      await expect(events).toHaveAttribute("aria-pressed", "true");

      // Each remove button names what it removes; removing the last tag leaves the empty text.
      const filters = selection.getByRole("list", { name: t("selection.filters") });
      await expect(filters.getByRole("listitem")).toHaveCount(3);
      for (const key of ["finance", "dubai", "thisYear"]) {
        const remove = selection.getByRole("button", { name: msg(locale, "ui.controls.remove", { name: t(`selection.${key}`) }) });
        await remove.focus();
        await page.keyboard.press("Enter");
        await expect(remove).toHaveCount(0);
      }
      await expect(selection.getByText(t("selection.noFilters"))).toBeVisible();
    });

    test("status parts carry their meaning in text and values", async ({ page }, testInfo) => {
      await openGallery(page, tenant, locale);
      const status = page.locator("section:has(> h2#cg-status)");

      // A status dot is never colour alone.
      await expect(status.locator(".ui-status-dot").first()).toHaveText(t("status.online"));
      await expect(status.getByRole("status").first()).toHaveText(t("status.loading"));

      // Progress with a value says how far; without one it is indeterminate. Meters report their value text.
      const upload = status.getByRole("progressbar", { name: t("status.upload") });
      await expect(upload).toHaveAttribute("value", "24");
      await expect(upload).toHaveAttribute("aria-valuetext", t("status.uploadValue", { done: "24", total: "60" }));
      await expect(status.getByRole("progressbar", { name: t("status.preparing") })).not.toHaveAttribute("value");
      const quota = status.getByRole("meter", { name: t("status.quota") });
      await expect(quota).toHaveAttribute("aria-valuenow", "410");
      await expect(quota).toHaveAttribute("aria-valuetext", t("status.quotaValue", { used: "410", total: "500" }));

      // The avatar group shows four people and says how many more there are, in words.
      const team = status.getByRole("list", { name: t("status.team") });
      await expect(team.getByRole("listitem")).toHaveCount(5);
      await expect(team.getByRole("listitem").last()).toHaveText(t("status.more", { count: "2" }));
      await expect(status.getByRole("separator")).toHaveText(t("status.or"));
      await expectAccessible(page, testInfo, `components-status-${locale}`);
    });

    test("the accordion, the announcement and pagination work from the keyboard", async ({ page }) => {
      await openGallery(page, tenant, locale);
      const structure = page.locator("section:has(> h2#cg-structure)");

      // Accordion: Enter opens a question and, in an exclusive group, closes the one that was open.
      const items = structure.locator(".ui-accordion details");
      await expect(items.nth(0)).toHaveAttribute("open", "");
      await expect(structure.getByText(t("structure.a1"))).toBeVisible();
      await items.nth(1).locator("summary").focus();
      await page.keyboard.press("Enter");
      await expect(structure.getByText(t("structure.a2"))).toBeVisible();
      await expect(items.nth(0)).not.toHaveAttribute("open");
      await expect(structure.getByText(t("structure.a1"))).toBeHidden();

      // Pagination: a named navigation of links; the current page is marked; the ends lead to the neighbors.
      const pages = structure.getByRole("navigation", { name: t("structure.pages") });
      await expect(pages.getByRole("link", { name: msg(locale, "ui.controls.page", { page: "6" }), exact: true })).toHaveAttribute("aria-current", "page");
      await expect(pages.getByRole("link", { name: msg(locale, "ui.controls.previous") })).toHaveAttribute("href", "#page-5");
      await expect(pages.getByRole("link", { name: msg(locale, "ui.controls.next") })).toHaveAttribute("href", "#page-7");
      await expect(pages.getByRole("link")).toHaveCount(7);

      // Announcement: a named region with one action; the close button is named and removes it.
      const banner = structure.getByRole("complementary", { name: t("structure.announceTitle") });
      await expect(banner.getByRole("link", { name: t("structure.announceAction") })).toBeVisible();
      const close = banner.getByRole("button", { name: msg(locale, "ui.controls.close") });
      await close.focus();
      await page.keyboard.press("Enter");
      await expect(banner).toHaveCount(0);
      await expect(structure.getByRole("status")).toHaveText(t("structure.dismissed"));
    });

    test("the dialog, drawer, popover and tooltip move focus in, close on Escape and return focus", async ({ page }) => {
      await openGallery(page, tenant, locale);
      const overlays = page.locator("section:has(> h2#cg-overlays)");

      // Dialog: modal, named by its title and described by its text; Escape closes and focus goes back to the button.
      const invite = overlays.getByRole("button", { name: t("overlays.openDialog") });
      await invite.focus();
      await page.keyboard.press("Enter");
      const dialog = page.getByRole("dialog", { name: t("overlays.dialogTitle") });
      await expect(dialog).toBeVisible();
      await expect(dialog).toHaveAccessibleDescription(t("overlays.dialogText"));
      expect(await dialog.evaluate((el) => el.matches(":modal") && el.contains(document.activeElement))).toBe(true);
      await page.keyboard.press("Escape");
      await expect(dialog).toBeHidden();
      await expect(invite).toBeFocused();

      // A click on the backdrop closes it; the main action closes it and shows a notification card.
      await invite.click();
      await expect(dialog).toBeVisible();
      await page.mouse.click(4, 4);
      await expect(dialog).toBeHidden();
      await invite.click();
      await dialog.getByRole("button", { name: t("overlays.send") }).click();
      await expect(dialog).toBeHidden();
      const card = page.locator(".ui-message[data-card]");
      await expect(card).toContainText(t("overlays.invited"));
      await expect(card).toContainText(t("overlays.invitedText"));
      await card.getByRole("button", { name: msg(locale, "ui.messages.dismiss") }).click();
      await expect(card).toHaveCount(0);

      // Drawer: the same modal behavior, attached to the end edge in the reading direction.
      const filters = overlays.getByRole("button", { name: t("overlays.openDrawer") });
      await filters.click();
      const drawer = page.getByRole("dialog", { name: t("overlays.drawerTitle") });
      await expect(drawer).toBeVisible();
      await expect
        .poll(async () => {
          const box = (await drawer.boundingBox())!;
          const width = page.viewportSize()!.width;
          return locale === "ar" ? Math.round(box.x) : Math.round(width - box.x - box.width);
        })
        .toBeLessThanOrEqual(1);
      await page.keyboard.press("Escape");
      await expect(drawer).toBeHidden();
      await expect(filters).toBeFocused();

      // Popover: the button says it is expanded; Tab goes into the panel; Escape closes and returns focus.
      const display = overlays.getByRole("button", { name: t("overlays.popover"), exact: true });
      await expect(display).toHaveAttribute("aria-expanded", "false");
      await display.focus();
      await page.keyboard.press("Enter");
      await expect(display).toHaveAttribute("aria-expanded", "true");
      const panel = page.getByRole("dialog", { name: t("overlays.popoverTitle") });
      await expect(panel).toBeVisible();
      await page.keyboard.press("Tab");
      await expect(panel.getByRole("switch", { name: t("overlays.compact") })).toBeFocused();
      await page.keyboard.press("Escape");
      await expect(panel).toBeHidden();
      await expect(display).toBeFocused();
      await expect(display).toHaveAttribute("aria-expanded", "false");
      // A second press of the button closes it, and so does a click outside.
      await display.click();
      await expect(panel).toBeVisible();
      await display.click();
      await expect(panel).toBeHidden();
      await display.click();
      await overlays.getByRole("heading", { level: 2 }).click();
      await expect(panel).toBeHidden();

      // Tooltip: shows on keyboard focus, describes the control, and Escape hides it without moving focus.
      const exportButton = overlays.getByRole("button", { name: t("overlays.export"), exact: true });
      await expect(exportButton).toHaveAccessibleDescription(t("overlays.exportTip"));
      const tip = overlays.getByRole("tooltip", { includeHidden: true });
      await expect(tip).toBeHidden();
      await display.focus();
      await page.keyboard.press("Tab");
      await expect(exportButton).toBeFocused();
      await expect(tip).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(tip).toBeHidden();
      await expect(exportButton).toBeFocused();
    });

    test("the command palette opens from the shortcut, filters and runs a command", async ({ page }) => {
      await openGallery(page, tenant, locale);
      await page.keyboard.press("Control+K");
      const palette = page.getByRole("dialog", { name: t("overlays.paletteLabel") });
      await expect(palette).toBeVisible();
      const box = palette.getByRole("combobox");
      await expect(box).toBeFocused();

      // Focus stays in the box; the arrow keys move the highlighted option.
      const options = palette.getByRole("option");
      await expect(options).toHaveCount(7);
      await expect(options.nth(0)).toHaveAttribute("aria-selected", "true");
      await page.keyboard.press("ArrowDown");
      await expect(options.nth(1)).toHaveAttribute("aria-selected", "true");
      await expect(box).toHaveAttribute("aria-activedescendant", (await options.nth(1).getAttribute("id"))!);
      await page.keyboard.press("ArrowUp");
      await page.keyboard.press("ArrowUp");
      await expect(options.nth(6)).toHaveAttribute("aria-selected", "true");
      await expect(box).toBeFocused();

      // Typing filters; nothing found is said in words; Enter runs the highlighted command and closes.
      await box.fill("zzzz");
      await expect(options).toHaveCount(0);
      await expect(palette.getByRole("status")).toHaveText(t("overlays.paletteEmpty"));
      await box.fill(t("overlays.commandLeave"));
      await expect(options).toHaveCount(1);
      await page.keyboard.press("Enter");
      await expect(palette).toBeHidden();
      await expect(page.getByTestId("cg-chosen")).toHaveText(t("menus.chosen", { name: t("overlays.commandLeave") }));

      // Escape closes it and the next opening starts empty.
      await page.keyboard.press("Control+K");
      await expect(box).toHaveValue("");
      await page.keyboard.press("Escape");
      await expect(palette).toBeHidden();
    });

    test("menus, submenus, the context menu and the collapsed breadcrumb work from the keyboard", async ({ page }) => {
      await openGallery(page, tenant, locale);
      const menus = page.locator("section:has(> h2#cg-menus)");
      const chosen = page.getByTestId("cg-chosen");

      // Opening moves focus to the first item; arrows wrap; Home and End jump.
      const trigger = menus.getByRole("button", { name: t("menus.actions"), exact: true });
      await expect(trigger).toHaveAttribute("aria-haspopup", "menu");
      await trigger.focus();
      await page.keyboard.press("Enter");
      const menu = page.getByRole("menu", { name: t("menus.actions"), exact: true });
      await expect(menu).toBeVisible();
      await expect(trigger).toHaveAttribute("aria-expanded", "true");
      const item = (key: string) => menu.getByRole("menuitem", { name: t(`menus.${key}`), exact: true });
      await expect(item("open")).toBeFocused();
      await page.keyboard.press("ArrowUp");
      await expect(item("delete")).toBeFocused();
      await page.keyboard.press("Home");
      await expect(item("open")).toBeFocused();
      await page.keyboard.press("End");
      await expect(item("delete")).toBeFocused();
      await page.keyboard.press("ArrowDown");
      await expect(item("open")).toBeFocused();
      if (locale === "en") {
        // A letter jumps to the next item that starts with it.
        await page.keyboard.press("d");
        await expect(item("duplicate")).toBeFocused();
        await page.keyboard.press("d");
        await expect(item("delete")).toBeFocused();
      }

      // The forward arrow opens the submenu and the back arrow leaves it, following the reading direction.
      await item("share").focus();
      await page.keyboard.press(forward);
      const share = page.getByRole("menu", { name: t("menus.share"), exact: true });
      await expect(share).toBeVisible();
      await expect(share.getByRole("menuitem", { name: t("menus.shareLink") })).toBeFocused();
      await page.keyboard.press(back);
      await expect(share).toBeHidden();
      await expect(item("share")).toBeFocused();

      // A disabled item can be focused but does nothing.
      await item("moveLocked").focus();
      await expect(item("moveLocked")).toHaveAttribute("aria-disabled", "true");
      await page.keyboard.press("Enter");
      await expect(menu).toBeVisible();

      // Enter runs an item, closes the menu and returns focus to the button.
      await item("rename").focus();
      await page.keyboard.press("Enter");
      await expect(menu).toBeHidden();
      await expect(trigger).toBeFocused();
      await expect(trigger).toHaveAttribute("aria-expanded", "false");
      await expect(chosen).toHaveText(t("menus.chosen", { name: t("menus.rename") }));

      // A submenu item runs too; a checkbox item keeps its state; Escape closes without choosing.
      await page.keyboard.press("ArrowDown");
      await item("share").focus();
      await page.keyboard.press("Enter");
      await share.getByRole("menuitem", { name: t("menus.shareEmail") }).click();
      await expect(menu).toBeHidden();
      await expect(chosen).toHaveText(t("menus.chosen", { name: t("menus.shareEmail") }));
      await trigger.click();
      const comments = menu.getByRole("menuitemcheckbox", { name: t("menus.showComments") });
      await expect(comments).toHaveAttribute("aria-checked", "false");
      await comments.click();
      await trigger.click();
      await expect(comments).toHaveAttribute("aria-checked", "true");
      await page.keyboard.press("Escape");
      await expect(menu).toBeHidden();
      await expect(trigger).toBeFocused();

      // Context menu: the secondary button opens the same list on the thing itself.
      const file = menus.getByRole("button", { name: t("menus.file") });
      await file.click({ button: "right" });
      const context = page.getByRole("menu", { name: t("menus.contextLabel") });
      await expect(context).toBeVisible();
      await expect(context.getByRole("menuitem", { name: t("menus.open"), exact: true })).toBeFocused();
      await page.keyboard.press("ArrowDown");
      await page.keyboard.press("ArrowDown");
      await page.keyboard.press("Enter");
      await expect(context).toBeHidden();
      await expect(chosen).toHaveText(t("menus.chosen", { name: t("menus.duplicate") }));

      // Breadcrumb: first level, a menu with the hidden levels, and the last two; the page itself is marked.
      const trail = menus.getByRole("navigation", { name: t("menus.trail") });
      await expect(trail.getByRole("link")).toHaveCount(3);
      await expect(trail.getByRole("link", { name: t("menus.crumbPage") })).toHaveAttribute("aria-current", "page");
      await trail.getByRole("button", { name: msg(locale, "ui.controls.moreLevels") }).click();
      const levels = page.getByRole("menu", { name: msg(locale, "ui.controls.moreLevels") });
      await expect(levels.getByRole("menuitem")).toHaveCount(3);
      await expect(levels.getByRole("menuitem", { name: t("menus.crumbFinance") })).toHaveAttribute("href", "#crumbFinance");
      await page.keyboard.press("Escape");
      await expect(levels).toBeHidden();
    });
    test("search, number, code, colour and combobox fields work from the keyboard", async ({ page }) => {
      await openGallery(page, tenant, locale);
      const inputs = page.locator("section:has(> h2#cg-inputs)");

      // Leading and trailing parts sit in one box; the label still names the text box.
      await expect(inputs.getByRole("textbox", { name: t("inputs.amount") })).toHaveValue("1250.00");

      // Search: the clear button appears with text, Escape clears, Enter searches.
      const search = inputs.getByRole("searchbox", { name: t("inputs.search") });
      await search.fill("Layla");
      const clear = inputs.getByRole("button", { name: msg(locale, "ui.controls.clear") });
      await expect(clear).toBeVisible();
      await page.keyboard.press("Enter");
      await expect(page.getByTestId("cg-searched")).toHaveText(t("inputs.searched", { term: "Layla" }));
      await page.keyboard.press("Escape");
      await expect(search).toHaveValue("");
      await expect(clear).toHaveCount(0);

      // Number: a spin button; arrows step, End goes to the limit, typing past the limit is brought back on leaving.
      const days = inputs.getByRole("spinbutton", { name: t("inputs.days") });
      await expect(days).toHaveAttribute("aria-valuenow", "5");
      await days.focus();
      await page.keyboard.press("ArrowUp");
      await expect(days).toHaveValue("6");
      await page.keyboard.press("End");
      await expect(days).toHaveValue("22");
      await expect(inputs.getByRole("button", { name: msg(locale, "ui.controls.increase") })).toBeDisabled();
      await days.fill("30");
      await expect(days).toHaveAttribute("aria-invalid", "true");
      await expect(inputs.getByText(t("inputs.daysError"))).toBeVisible();
      await page.keyboard.press("Tab");
      await expect(days).toHaveValue("22");

      // One-time code: typing moves on, Backspace moves back, a paste fills every box.
      const code = inputs.getByRole("group", { name: t("inputs.code") });
      const digit = (n: number) => code.getByRole("textbox", { name: msg(locale, "ui.controls.digit", { index: String(n), count: "6" }) });
      await digit(1).focus();
      await page.keyboard.type("12");
      await expect(digit(3)).toBeFocused();
      await page.keyboard.press("Backspace");
      await expect(digit(2)).toBeFocused();
      await expect(digit(2)).toHaveValue("");
      await digit(1).evaluate((el) => {
        const data = new DataTransfer();
        data.setData("text", "48 31 07");
        el.dispatchEvent(new ClipboardEvent("paste", { clipboardData: data, bubbles: true, cancelable: true }));
      });
      await expect(page.getByTestId("cg-code")).toHaveText(t("inputs.codeDone", { code: "483107" }));

      // Colour: presets are named toggle buttons that write the code.
      const green = inputs.getByRole("button", { name: t("inputs.presetGreen") });
      await green.click();
      await expect(green).toHaveAttribute("aria-pressed", "true");
      await expect(inputs.getByRole("textbox", { name: t("inputs.color"), exact: true })).toHaveValue("#0b6e4f");

      // Combobox: typing filters, the arrows move the highlight, Enter chooses, Escape closes the list.
      const department = inputs.getByRole("combobox", { name: t("inputs.manager") });
      await expect(department).toHaveValue(t("inputs.roleFinance"));
      await department.fill("");
      await expect(department).toHaveAttribute("aria-expanded", "true");
      const list = page.getByRole("listbox", { name: t("inputs.manager") });
      await expect(list.getByRole("option")).toHaveCount(6);
      await department.fill(t("inputs.roleLegal").slice(0, 3));
      await expect(list.getByRole("option")).toHaveCount(1);
      await page.keyboard.press("Enter");
      await expect(department).toHaveValue(t("inputs.roleLegal"));
      await expect(department).toHaveAttribute("aria-expanded", "false");
      await department.fill("zzzz");
      await expect(list.getByText(t("inputs.managerEmpty"))).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(department).toHaveAttribute("aria-expanded", "false");

      // Upload: the drop area is a labelled file input; each file names its remove button; errors are words.
      await expect(inputs.getByText(t("inputs.uploadTooLarge"))).toBeVisible();
      await inputs.getByLabel(t("inputs.upload")).setInputFiles({ name: "Leave form.pdf", mimeType: "application/pdf", buffer: Buffer.from("%PDF-1.4") });
      const files = inputs.getByRole("list", { name: msg(locale, "ui.controls.chosenFiles") });
      await expect(files.getByRole("listitem")).toHaveCount(3);
      await inputs.getByRole("button", { name: msg(locale, "ui.controls.remove", { name: t("inputs.fileBudget") }) }).click();
      await expect(files.getByRole("listitem")).toHaveCount(2);
    });

    test("calendars, date fields, slots, the carousel and the tree work from the keyboard", async ({ page }) => {
      await openGallery(page, tenant, locale);
      const dates = page.locator("section:has(> h2#cg-dates)");

      // Calendar: a named grid; the arrows follow the reading direction and skip nothing; today is marked.
      const grid = dates.getByRole("grid", { name: t("dates.gregorian"), exact: true });
      const chosen = grid.locator('[data-date="2026-11-12"]');
      await expect(chosen).toHaveAttribute("tabindex", "0");
      await expect(grid.locator('[data-date="2026-11-10"]')).toHaveAttribute("aria-current", "date");
      await chosen.focus();
      await page.keyboard.press(forward);
      await expect(grid.locator('[data-date="2026-11-13"]')).toBeFocused();
      await page.keyboard.press("ArrowDown");
      await expect(grid.locator('[data-date="2026-11-20"]')).toBeFocused();
      // Weekend days can be reached but not chosen.
      await expect(grid.locator('[data-date="2026-11-20"]')).toHaveAttribute("aria-disabled", "true");
      await page.keyboard.press("Enter");
      await expect(grid.locator('[data-date="2026-11-12"]').locator("xpath=..")).toHaveAttribute("aria-selected", "true");
      await page.keyboard.press(back);
      await page.keyboard.press("Enter");
      await expect(grid.locator('[data-date="2026-11-19"]').locator("xpath=..")).toHaveAttribute("aria-selected", "true");
      // Page Down goes to the next month and keeps the day.
      await page.keyboard.press("PageDown");
      await expect(grid.locator('[data-date="2026-12-19"]')).toBeFocused();

      // The Hijri calendar shows the same days in its own months, through Intl.
      const hijri = dates.getByRole("grid", { name: t("dates.hijri") });
      const hijriMonth = new Intl.DateTimeFormat(locale, { calendar: "islamic-umalqura", month: "long", year: "numeric", timeZone: "UTC" }).format(Date.parse("2026-11-15T00:00:00Z"));
      // The month title describes the grid.
      await expect(hijri).toHaveAccessibleDescription(hijriMonth);
      await expect(hijri.locator('[data-range="middle"]')).toHaveCount(3);

      // Date field: opens the calendar on the right day, and choosing returns focus to the field.
      const field = dates.getByRole("button", { name: t("dates.date") });
      await field.click();
      await expect(field).toHaveAttribute("aria-expanded", "true");
      const picker = page.getByRole("dialog", { name: t("dates.date") });
      await expect(picker.locator('.ui-calendar-day[tabindex="0"]')).toBeFocused();
      await picker.locator('[data-date="2026-11-24"]').click();
      await expect(picker).toBeHidden();
      await expect(field).toBeFocused();
      const long = new Intl.DateTimeFormat(locale, { dateStyle: "long", timeZone: "UTC" }).format(Date.parse("2026-11-24T00:00:00Z"));
      await expect(field).toContainText(long);

      // Range field: two choices make the range and close it.
      const rangeField = dates.getByRole("button", { name: t("dates.range") });
      await rangeField.click();
      const rangePicker = page.getByRole("dialog", { name: t("dates.range") });
      await rangePicker.locator('[data-date="2026-11-23"]').click();
      await expect(rangePicker).toBeVisible();
      await rangePicker.locator('[data-date="2026-11-25"]').click();
      await expect(rangePicker).toBeHidden();
      await expect(page.locator('input[name="cg-range_start"]')).toHaveValue("2026-11-23");
      await expect(page.locator('input[name="cg-range_end"]')).toHaveValue("2026-11-25");

      // Slots: a radio group; taken slots are disabled and say why.
      const slots = dates.getByRole("group", { name: t("dates.slots") });
      const ten30 = slots.getByRole("radio", { checked: true });
      await ten30.focus();
      await page.keyboard.press("ArrowDown");
      await expect(slots.getByRole("radio", { checked: true })).not.toHaveValue("10:30");
      await expect(slots.getByRole("radio", { disabled: true })).toHaveCount(2);
      await expect(slots.getByRole("radio", { name: new RegExp(t("dates.booked")) })).toHaveCount(2);

      // Carousel: a named list; previous starts off; next moves one card along.
      const carousel = dates.getByRole("list", { name: t("dates.carousel") });
      await expect(carousel.getByRole("listitem")).toHaveCount(5);
      const previous = dates.getByRole("button", { name: msg(locale, "ui.controls.previousSlide"), exact: true });
      await expect(previous).toBeDisabled();
      await dates.getByRole("button", { name: msg(locale, "ui.controls.nextSlide"), exact: true }).click();
      await expect(previous).toBeEnabled();

      // Tree: one item in the tab order; arrows move and open branches in the reading direction.
      const tree = dates.getByRole("tree", { name: t("dates.tree") });
      const item = (key: string) => tree.getByRole("treeitem", { name: t(`dates.${key}`), exact: true });
      const travel = tree.locator('[data-key="nodeTravel"]');
      await expect(travel).toHaveAttribute("tabindex", "0");
      await expect(travel).toHaveAttribute("aria-selected", "true");
      await travel.focus();
      await page.keyboard.press(back);
      await expect(tree.locator('[data-key="nodeFinance"]')).toBeFocused();
      await page.keyboard.press(back);
      await expect(tree.locator('[data-key="nodeFinance"]')).toHaveAttribute("aria-expanded", "false");
      await page.keyboard.press("ArrowDown");
      await expect(tree.locator('[data-key="nodeIt"]')).toBeFocused();
      await page.keyboard.press(forward);
      await expect(tree.locator('[data-key="nodeIt"]')).toHaveAttribute("aria-expanded", "true");
      await page.keyboard.press(forward);
      await expect(tree.locator('[data-key="nodeSecurity"]')).toBeFocused();
      await page.keyboard.press("Enter");
      await expect(page.getByTestId("cg-node")).toHaveText(t("dates.treeChosen", { name: t("dates.nodeSecurity") }));
      await expect(item("nodeSecurity")).toHaveAttribute("aria-selected", "true");
    });
  });
}
