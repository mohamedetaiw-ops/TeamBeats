import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 010 R2 T014 (US6 scenarios 1 to 3): Layla adds a skill from the tenant's list on her own profile; Omar opens her
 * profile and endorses it; Find an expert lists her for the skill. The rules (ranking, hiding, off and purge) are
 * covered by SkillTests. In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`skills in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let layla: string;
    const skill = locale === "ar" ? "الترجمة القانونية" : "Legal translation";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`skl-${locale}`, locale, "workplace_plus", ["layla", "omar"]);
      const admin = await tenantApi(tenant);
      const current = await admin.get("/api/v1/people/skills");
      const saved = await admin.put("/api/v1/people/skills", {
        headers: { "if-match": current.headers()["etag"] ?? "" },
        data: { skills: [{ names: { en: "Legal translation", ar: "الترجمة القانونية" } }, { names: { en: "Excel", ar: "إكسل" } }] },
      });
      expect(saved.ok(), await saved.text()).toBe(true);
      await admin.dispose();
      const client = await tenantApiAs(tenant, `layla@${tenant.key}.example`);
      layla = ((await (await client.get("/api/v1/me/profile")).json()) as { profile: { id: string } }).profile.id;
      await client.dispose();
    });

    test("Layla adds a skill to her profile", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/people/me`);
      await page.getByLabel(m("people.skills.add"), { exact: true }).selectOption({ label: skill });
      await page.getByRole("button", { name: m("people.skills.addButton"), exact: true }).click();
      await page.getByLabel(m("people.skills.levelFor", { skill })).selectOption("expert");
      await page.getByRole("button", { name: m("people.skills.save") }).click();
      await expect(page.getByText(m("people.skills.saved"))).toBeVisible();
      await expectAccessible(page, testInfo, `people-own-skills-${locale}`);
    });

    test("Omar endorses it and finds Layla as an expert", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `omar@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/people/${layla}`);
      const skills = page.getByTestId("person-skills");
      await expect(skills).toContainText(skill);
      await expect(skills).toContainText(m("people.skills.level.expert"));
      await skills.getByRole("button", { name: m("people.skills.endorseLabel", { skill }) }).click();
      await expect(skills.getByRole("button", { name: m("people.skills.unendorseLabel", { skill }) })).toHaveAttribute("aria-pressed", "true");
      await expectAccessible(page, testInfo, `people-skills-${locale}`);

      await page.getByRole("navigation", { name: m("people.nav.label") }).getByRole("link", { name: m("people.nav.experts") }).click();
      await page.waitForURL(/\/people\/experts/);
      await page.getByRole("search").getByLabel(m("people.experts.search"), { exact: true }).fill(locale === "ar" ? "القانونية" : "legal");
      await page.getByRole("button", { name: m("people.experts.find") }).click();
      const experts = page.getByTestId("experts");
      await expect(experts.getByRole("listitem")).toHaveCount(1);
      await expect(experts).toContainText(skill);
      await expectAccessible(page, testInfo, `people-experts-${locale}`);
    });
  });
}
