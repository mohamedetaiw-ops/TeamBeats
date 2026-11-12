import { request } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/** Signs the person in once through the API so that they exist in the people directory. */
async function signInOnce(tenant: TestTenant, email: string): Promise<void> {
  const context = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": `${tenant.key}.${stack.baseDomain}` } });
  const response = await context.post("/dev/signin", { data: { email } });
  await context.dispose();
  if (!response.ok()) throw new Error(`Sign-in failed with ${response.status()}.`);
}

/**
 * Spec 023 T012 (team and star of the period): the administrator opens an award for the whole organization,
 * nominates Layla with a reason, reviews the nominations as an approver, chooses Layla and sees them as the winner
 * with a link to the announcement draft. Scope, self nomination, closing and approvers in scope are covered by
 * AwardTests. In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`awards in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const title = locale === "ar" ? "نجم الربع" : "Star of the quarter";

    test.beforeAll(async () => {
      tenant = await createTenant(`aw-${locale}`, locale, "workplace_plus", ["layla"]);
      await signInOnce(tenant, `layla@${tenant.key}.example`);
    });

    test("an award is opened, a colleague is nominated and chosen, and the announcement draft is linked", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities`);
      await page.getByRole("link", { name: m("engagement.awards.link") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.awards.pageTitle"));

      const open = page.getByRole("region", { name: m("engagement.awards.openTitle") });
      await open.getByLabel(m("engagement.awards.title")).fill(title);
      const closeOn = new Date(Date.now() + 7 * 86_400_000).toISOString().slice(0, 10);
      await open.getByLabel(m("engagement.awards.closeOn")).fill(closeOn);
      await expectAccessible(page, testInfo, `awards-open-${locale}`);
      await open.getByRole("button", { name: m("engagement.awards.open") }).click();

      const card = page.getByRole("list", { name: m("engagement.awards.listLabel") }).getByRole("article").first();
      await expect(card.getByRole("heading", { name: title })).toBeVisible();
      const form = card.getByRole("form", { name: m("engagement.awards.nominateLabel", { award: title }) });
      await form.getByLabel(m("services.person.search")).fill("layla");
      await form.getByRole("button", { name: m("services.person.find") }).click();
      await form.getByRole("list", { name: m("services.person.results") }).getByRole("button").first().click();
      await form.getByLabel(m("engagement.awards.reason")).fill("Led the month-end closing with patience");
      await form.getByRole("button", { name: m("engagement.awards.nominate") }).click();
      await expect(card.getByText(m("engagement.awards.myNomination"))).toBeVisible();

      await card.getByRole("button", { name: m("engagement.awards.review") }).click();
      const choose = card.getByRole("group", { name: m("engagement.awards.chooseWinners") });
      await expect(choose.getByText("Led the month-end closing with patience")).toBeVisible();
      await choose.getByRole("checkbox").first().check();
      await expectAccessible(page, testInfo, `awards-decide-${locale}`);
      await choose.getByRole("button", { name: m("engagement.awards.confirmWinners") }).click();

      await expect(card.getByText(m("engagement.awards.statusDecided"))).toBeVisible();
      await expect(card.getByRole("list", { name: m("engagement.awards.winners") })).toContainText(/layla/i);
      await expect(card.getByRole("link", { name: m("engagement.awards.announcement") })).toBeVisible();
    });
  });
}
