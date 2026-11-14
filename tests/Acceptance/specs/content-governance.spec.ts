import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

const paragraph = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });

async function createItem(api: APIRequestContext, type: string, locale: LaunchLocale, title: string, body: string, critical?: object): Promise<string> {
  const response = await api.post("/api/v1/content/items", { data: { type, variants: { [locale]: { title, body: paragraph(body) } }, critical } });
  expect(response.status()).toBe(201);
  return ((await response.json()) as { id: string }).id;
}

async function publish(api: APIRequestContext, id: string) {
  expect((await api.post(`/api/v1/content/items/${id}/publish`, { data: { material: true } })).ok()).toBe(true);
}

/** Waits until the background audience step has put the announcement in the person's pending list. */
async function pendingFor(api: APIRequestContext, id: string) {
  await expect
    .poll(async () => {
      const pending = (await (await api.get("/api/v1/content/critical/pending")).json()) as { modal: { id: string } | null; banners: { id: string }[] };
      return pending.modal?.id === id || pending.banners.some((b) => b.id === id);
    })
    .toBe(true);
}

/**
 * Spec 009 T023 and US2: a critical pop-up and banner show in the shell and are answered there, the publisher sees the
 * confirmations; an item's history compares two versions word by word and restores an older one; content health
 * lists the sites. In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`content governance in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`cgv-${locale}`, locale, "workplace");
    });

    test("a critical pop-up and banner are answered in the shell and counted for the publisher", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const popupTitle = locale === "ar" ? "تحديث سياسة السلامة" : "Safety policy update";
      const bannerTitle = locale === "ar" ? "إغلاق موقف السيارات" : "Car park closed";
      const popup = await createItem(api, "announcement", locale, popupTitle, popupTitle, { presentation: "modal", remindLaterMax: 2 });
      await publish(api, popup);
      const banner = await createItem(api, "announcement", locale, bannerTitle, bannerTitle, { presentation: "banner", requireAck: true });
      await publish(api, banner);
      await pendingFor(api, popup);
      await pendingFor(api, banner);

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const dialog = page.getByRole("dialog", { name: popupTitle });
      await expect(dialog).toBeVisible();
      await expect(dialog.getByText(popupTitle).last()).toBeVisible();
      await expectAccessible(page, testInfo, `content-critical-modal-${locale}`);
      // Escape does not close a pop-up that needs an answer.
      await page.keyboard.press("Escape");
      await expect(dialog).toBeVisible();
      await dialog.getByRole("button", { name: msg(locale, "content.critical.remindLater", { count: "2" }) }).click();
      await expect(dialog).toBeHidden();

      const strip = page.getByRole("region", { name: bannerTitle });
      await expect(strip).toBeVisible();
      await page.goto(`${hostOrigin(tenant.key)}/people`);
      await expect(page.getByRole("region", { name: bannerTitle })).toBeVisible();
      await expectAccessible(page, testInfo, `content-critical-banner-${locale}`);
      await page.getByRole("region", { name: bannerTitle }).getByRole("button", { name: msg(locale, "content.critical.confirm") }).click();
      await expect(page.getByRole("region", { name: bannerTitle })).toBeHidden();

      await page.goto(`${hostOrigin(tenant.key)}/content/items/${banner}/acknowledgements`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "content.acks.heading", { title: bannerTitle }));
      const confirmed = page.getByRole("definition").nth(1);
      await expect(confirmed).toContainText(new Intl.NumberFormat(locale).format(1));
      await expectAccessible(page, testInfo, `content-acks-${locale}`);
      await page.getByRole("button", { name: msg(locale, "content.acks.requestReport") }).click();
      await expect(page.getByRole("link", { name: new RegExp(msg(locale, "content.acks.download", { rows: "\\d+" }).replace(/[()]/g, "\\$&")) })).toBeVisible({
        timeout: 30_000,
      });
      await api.dispose();
    });

    test("history compares two versions and restores an older one; content health lists the sites", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const first = locale === "ar" ? "ساعات العمل الصيفية" : "Summer working hours";
      const second = locale === "ar" ? "ساعات العمل الشتوية" : "Winter working hours";
      const id = await createItem(api, "news", locale, first, first);
      await publish(api, id);
      const item = (await (await api.get(`/api/v1/content/items/${id}`)).json()) as { version: number };
      expect((await api.put(`/api/v1/content/items/${id}`, { data: { version: item.version, variants: { [locale]: { title: second, body: paragraph(second) } } } })).ok()).toBe(true);
      await publish(api, id);

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/items/${id}/history`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "content.history.heading", { title: second }));
      await page.getByRole("button", { name: msg(locale, "content.history.show") }).click();
      const diff = page.getByTestId("content-diff");
      await expect(diff.locator("ins").first()).toBeVisible();
      await expect(diff.locator("del").first()).toBeVisible();
      await expectAccessible(page, testInfo, `content-history-${locale}`);

      await page.getByRole("button", { name: msg(locale, "content.history.restoreVersion", { number: "1" }) }).click();
      await page.waitForURL(new RegExp(`/content/items/${id}$`));
      await expect(page.getByLabel(msg(locale, "content.items.title"))).toHaveValue(first);

      await page.goto(`${hostOrigin(tenant.key)}/content/health`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "content.health.title"));
      await expect(page.getByRole("table").getByRole("rowheader").first()).toBeVisible();
      await expectAccessible(page, testInfo, `content-health-${locale}`);

      // Retention per content type (FR-020): news keeps old versions for two years.
      const news = page.getByRole("group", { name: msg(locale, "content.health.types.news"), exact: true });
      await news.getByLabel(msg(locale, "content.health.retention.action")).selectOption("versions");
      await news.getByLabel(msg(locale, "content.health.retention.months")).fill("24");
      await page.getByRole("button", { name: msg(locale, "content.health.retention.save") }).click();
      await expect(page.getByText(msg(locale, "content.health.retention.saved"))).toBeVisible();
      await api.dispose();
    });
  });
}
