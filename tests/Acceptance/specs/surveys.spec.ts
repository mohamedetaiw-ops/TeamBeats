import { request, type APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

async function signedIn(tenant: TestTenant, email: string): Promise<APIRequestContext> {
  const host = `${tenant.key}.${stack.baseDomain}`;
  const anonymous = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
  const response = await anonymous.post("/dev/signin", { data: { email } });
  const token = /tb_session=([^;]+)/.exec(response.headers()["set-cookie"] ?? "")?.[1];
  await anonymous.dispose();
  if (!response.ok() || !token) throw new Error(`Sign-in failed with ${response.status()}.`);
  return request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host, cookie: `tb_session=${token}` } });
}

/**
 * Spec 023 T009 (surveys): the administrator builds a named survey for the People unit on the surveys page, saves the
 * draft and publishes it; Layla, in the unit, finds it under "To answer" in her own browser and answers; the
 * administrator then sees the count and who answered what. Anonymous surveys, thresholds and the export rules are
 * covered by SurveyTests. In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`surveys in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    const unitName = locale === "ar" ? "خدمات الموظفين" : "People services";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`sv-${locale}`, locale, "workplace_plus", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const layla = await signedIn(tenant, laylaEmail);
      const unit = await admin.post("/api/v1/org/units", {
        data: { parentId: null, typeKey: "department", code: "PEOPLE", names: { en: "People services", ar: "خدمات الموظفين" } },
      });
      expect(unit.ok(), await unit.text()).toBe(true);
      const unitId = ((await unit.json()) as { unit: { id: string } }).unit.id;
      for (const client of [admin, layla]) {
        const me = (await (await client.get("/api/v1/me")).json()) as { id: string };
        const member = await admin.post(`/api/v1/org/units/${unitId}/memberships`, { data: { userId: me.id, type: "primary" } });
        expect(member.ok(), await member.text()).toBe(true);
      }
      await layla.dispose();
      await admin.dispose();
    });

    test("the author builds and publishes a survey, a colleague answers, and the author reads the results", async ({ page, browser }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities`);
      await page.getByRole("link", { name: m("engagement.surveys.link") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.surveys.pageTitle"));

      await page.getByLabel(m("engagement.surveys.unit")).selectOption({ label: unitName });
      await page.getByLabel(m("engagement.surveys.titleField")).fill("Team offsite");
      await page.getByLabel(m("engagement.surveys.anonymous")).uncheck();
      const first = page.getByRole("group", { name: m("engagement.surveys.questionNumber", { number: "1" }) });
      await first.getByLabel(m("engagement.surveys.prompt")).fill("Which month suits you?");
      await first.getByLabel(m("engagement.surveys.options")).fill("March\nApril");
      await expectAccessible(page, testInfo, `surveys-build-${locale}`);
      await page.getByRole("button", { name: m("engagement.surveys.createDraft") }).click();

      await expect(page.getByRole("heading", { level: 1 })).toHaveText("Team offsite");
      await expect(page.getByText(m("engagement.surveys.status.draft"), { exact: true })).toBeVisible();
      await page.getByRole("button", { name: m("engagement.surveys.publish"), exact: true }).click();
      await expect(page.getByText(m("engagement.surveys.status.open"), { exact: true })).toBeVisible();
      const surveyUrl = page.url();

      const otherContext = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const other = await otherContext.newPage();
      await signInWithDevForm(other, tenant.key, laylaEmail, locale);
      await other.goto(`${hostOrigin(tenant.key)}/communities/surveys`);
      await other.getByRole("region", { name: m("engagement.surveys.toAnswer") }).getByRole("link", { name: "Team offsite" }).click();
      await expect(other.getByRole("heading", { level: 1 })).toHaveText("Team offsite");
      await other.getByRole("radio", { name: "April" }).check();
      await expectAccessible(other, testInfo, `surveys-answer-${locale}`);
      await other.getByRole("button", { name: m("engagement.surveys.submit") }).click();
      await expect(other.getByText(m("engagement.surveys.thanks"))).toBeVisible();
      await otherContext.close();

      await page.goto(surveyUrl);
      const results = page.getByRole("region", { name: m("engagement.surveys.resultsTitle") });
      await expect(results.getByRole("progressbar", { name: "April" })).toHaveAttribute("value", "1");
      await expect(page.getByRole("region", { name: m("engagement.surveys.namedTitle") }).getByText(/layla/i)).toBeVisible();
      await page.getByRole("button", { name: m("engagement.surveys.close") }).click();
      await expect(page.getByText(m("engagement.surveys.status.closed"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, `surveys-results-${locale}`);
    });
  });
}
