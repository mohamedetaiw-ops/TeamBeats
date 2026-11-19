import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

const people = ["c1", "c2", "c3", "c4", "c5"];
const body = { blocks: [{ type: "paragraph", children: [{ text: "Who is in for the Friday run?" }] }] };

/**
 * Spec 021 T020 in the browser: five people post in a community; the administrator opens the engagement dashboard from
 * the analytics workspace and sees the posts and the community, then the knowledge, HR and security trend dashboards,
 * each with its heading and figures or empty state. In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`area analytics in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`are-${locale}`, locale, "workplace_plus", people);
      const admin = await tenantApi(tenant);
      const created = await admin.post("/api/v1/engagement/communities", { data: { names: { en: "Running club", ar: "نادي الجري" }, kind: "open" } });
      expect(created.status(), await created.text()).toBe(201);
      const community = ((await created.json()) as { id: string }).id;
      await admin.dispose();
      for (const person of people) {
        const client = await tenantApiAs(tenant, `${person}@${tenant.key}.example`);
        expect((await client.post(`/api/v1/engagement/communities/${community}:join`)).ok()).toBe(true);
        const posted = await client.post(`/api/v1/engagement/communities/${community}/posts`, { data: { body } });
        expect(posted.status(), await posted.text()).toBe(201);
        await client.dispose();
      }
    });

    test("an administrator reads the engagement, knowledge, HR and security dashboards", async ({ page }, testInfo) => {
      test.setTimeout(180_000);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/analytics`);
      await page.getByRole("link", { name: m("analytics.workspace.engagementLink") }).click();
      await page.waitForURL(/\/analytics\/engagement$/);
      await expect(page.getByRole("heading", { level: 1, name: m("analytics.areas.engagement.title") })).toBeVisible();
      const totals = page.getByTestId("area-totals");
      await expect(totals).toContainText(m("analytics.areas.engagement.total.posts"));
      await expect(totals).not.toContainText(m("analytics.areas.hidden", { size: "5" }).split(" (")[0]);
      await expect(page.getByTestId("area-engagement-communities")).toContainText(locale === "ar" ? "نادي الجري" : "Running club");
      await expectAccessible(page, testInfo, `analytics-engagement-${locale}`);

      for (const area of ["knowledge", "hr", "security"] as const) {
        await page.goto(`${hostOrigin(tenant.key)}/analytics/${area}`);
        await expect(page.getByRole("heading", { level: 1, name: m(`analytics.areas.${area}.title`) })).toBeVisible();
        await expectAccessible(page, testInfo, `analytics-${area}-${locale}`);
      }
      // A new tenant's policy settings are weaker than the baseline, so security trends always have figures.
      await expect(page.getByTestId("area-security-weakerChecks")).toContainText(m("analytics.areas.security.weakerChecks.keys.mfa_required"));
    });
  });
}
