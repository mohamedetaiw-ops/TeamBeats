import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 020 T014 and spec 027 T013: the agent pages. An administrator starts an agent from the Enterprise Knowledge
 * template, saves it, tries it in the test panel and sends it for review, which starts the evaluation gate; the gate
 * ends incomplete, and a second administrator, who is not the last editor, overrides it with a reason and publishes. As a member of the agent's unit the administrator then finds it under
 * Agents and gets an answer, asks the agent about a news item from its page, reads the run in the runs list and its timeline, and switches all agents off and on again
 * with the kill switch. In English and Arabic with axe on every screen. The API decides every rule; these steps check
 * what the pages send and show.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`agent pages in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let news: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const unitName = locale === "ar" ? "خدمات الموظفين" : "People services";
    const agentName = locale === "ar" ? "المعرفة المؤسسية" : "Enterprise Knowledge";
    const newName = locale === "ar" ? "مساعد السياسات" : "Policy helper";

    test.beforeAll(async () => {
      tenant = await createTenant(`agents-${locale}`, locale, "workplace_plus", ["rana"]);
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", {
        data: { parentId: null, typeKey: "department", code: "PEOPLE", names: { en: "People services", ar: "خدمات الموظفين" } },
      });
      expect(unit.ok(), await unit.text()).toBe(true);
      const unitId = ((await unit.json()) as { unit: { id: string } }).unit.id;
      const me = (await (await api.get("/api/v1/me")).json()) as { id: string };
      const member = await api.post(`/api/v1/org/units/${unitId}/memberships`, { data: { userId: me.id, type: "primary" } });
      if (!member.ok()) throw new Error(`Membership failed with ${member.status()}: ${await member.text()}`);
      const paragraph = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });
      const created = await api.post("/api/v1/content/items", {
        data: {
          type: "news",
          variants: {
            en: { title: "Annual leave changes", body: paragraph("Annual leave rises to 25 days from January.") },
            ar: { title: "تغييرات الإجازة السنوية", body: paragraph("تزيد الإجازة السنوية إلى 25 يوما من يناير.") },
          },
        },
      });
      expect(created.status(), await created.text()).toBe(201);
      news = ((await created.json()) as { id: string }).id;
      const published = await api.post(`/api/v1/content/items/${news}/publish`, { data: {} });
      expect(published.ok(), await published.text()).toBe(true);
      await api.dispose();
    });

    test("design, review, use and switch off an agent", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin`);
      await page.getByRole("link", { name: m("admin.nav.agents"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("agents.registry.title"));
      await expect(page.getByText(m("agents.registry.empty"))).toBeVisible();
      await expectAccessible(page, testInfo, `agents-registry-${locale}`);

      // Start from the template in the new unit.
      await page.getByLabel(m("agents.templates.unit")).selectOption({ label: unitName });
      await page.getByRole("button", { name: m("agents.templates.installNamed", { name: agentName }) }).click();
      await page.waitForURL(/\/admin\/agents\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(agentName);
      await expect(page.getByTestId("agent-status")).toHaveText(m("agents.status.draft"));
      await expectAccessible(page, testInfo, `agents-editor-${locale}`);

      const name = m("agents.editor.inLanguage", { field: m("agents.editor.name"), language: m(`agents.editor.languages.${locale}`) });
      await page.getByLabel(name, { exact: true }).fill(newName);
      await page.getByRole("button", { name: m("agents.editor.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("agents.editor.saved") })).toBeVisible();

      // The test panel runs the latest version as the designer.
      await page.getByLabel(m("agents.editor.test.message"), { exact: true }).fill(locale === "ar" ? "ما هي سياسة الإجازات؟" : "What is the leave policy?");
      await page.getByRole("button", { name: m("agents.editor.test.run") }).click();
      await expect(page.getByTestId("agent-test-result")).toBeVisible();

      await page.getByRole("button", { name: m("agents.editor.submit") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("agents.editor.submitted") })).toBeVisible();
      await expect(page.getByTestId("agent-status")).toHaveText(m("agents.status.in_review"));

      // Submitting started the evaluation gate, which runs on the workers. The template's own cases are too few for its
      // blocking checks, so the gate ends incomplete and blocks publishing; the last editor cannot override it.
      await page.getByRole("link", { name: m("agents.editor.openReview") }).click();
      await page.waitForURL(/\/review$/);
      const review = page.url();
      await expect(page.getByRole("heading", { name: m("agents.review.firstVersion") })).toBeVisible();
      await expect(async () => {
        await page.reload();
        await expect(page.getByTestId("review-gate")).toHaveText(m("agents.review.gateStatus.incomplete"), { timeout: 2_000 });
      }).toPass({ timeout: 90_000 });
      await expect(page.getByTestId("review-cannot-approve")).toHaveText(m("agents.review.cannot.gate_failed"));
      await expect(page.getByLabel(m("agents.review.overrideReason"), { exact: true })).toHaveCount(0);

      // Another administrator reads the result and the scores, overrides it with a reason, and publishes.
      await signInWithDevForm(page, tenant.key, `rana@${tenant.key}.example`, locale);
      await page.goto(review);
      await expect(page.getByTestId("review-gate-incomplete").locator("li").first()).toBeVisible();
      await expect(page.getByTestId("review-gate-diff")).toBeVisible();
      await expectAccessible(page, testInfo, `agents-review-${locale}`);
      await page.getByLabel(m("agents.review.overrideReason"), { exact: true }).fill(locale === "ar" ? "راجعنا الإجابات يدويا قبل النشر." : "We checked the answers by hand before publishing.");
      await page.getByRole("button", { name: m("agents.review.override") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("agents.review.overridden") })).toBeVisible();
      await expect(page.getByTestId("review-gate")).toHaveText(m("agents.review.gateStatus.overridden"));
      await page.getByRole("button", { name: m("agents.review.approve") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("agents.review.published") })).toBeVisible();
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);

      // A member of the unit finds the agent and gets an answer.
      await page.goto(`${hostOrigin(tenant.key)}/agents`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("agents.use.title"));
      await expectAccessible(page, testInfo, `agents-available-${locale}`);
      await page.getByTestId("agents-available").getByRole("link", { name: newName }).click();
      await page.waitForURL(/\/agents\/[0-9a-f-]{36}$/);
      await page.getByLabel(m("agents.use.message"), { exact: true }).fill(locale === "ar" ? "كم يوما للإجازة السنوية؟ www.example.org/leave" : "How many days of annual leave do I have? www.example.org/leave");
      await page.getByRole("button", { name: m("agents.use.send") }).click();
      await expect(page.getByTestId("agent-answer")).toBeVisible();
      // A web address in an answer stays text, with a warning (spec 027 US5 scenario 3).
      await expect(page.getByText(m("agents.use.linksAsText"))).toBeVisible();
      await expect(page.getByTestId("agent-answer").locator("a, img")).toHaveCount(0);
      await expectAccessible(page, testInfo, `agents-conversation-${locale}`);

      // The contextual action on a news item passes the item to the agent the person picks.
      await page.goto(`${hostOrigin(tenant.key)}/content/read/${news}`);
      await page.getByRole("link", { name: m("agents.use.ask") }).click();
      await expect(page.getByText(m("agents.use.chooseForItem"))).toBeVisible();
      await page.getByTestId("agents-available").getByRole("link", { name: newName }).click();
      await expect(page.getByText(m("agents.use.withItem"))).toBeVisible();
      await page.getByLabel(m("agents.use.message"), { exact: true }).fill(locale === "ar" ? "لخص هذا الخبر" : "Summarize this news");
      await page.getByRole("button", { name: m("agents.use.send") }).click();
      await expect(page.getByTestId("agent-answer")).toBeVisible();

      // The runs list holds the test run and the two real ones; the timeline shows the steps.
      await page.goto(`${hostOrigin(tenant.key)}/admin/agents/runs`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("agents.runs.title"));
      await expect(page.getByTestId("agents-runs").locator("tbody tr")).toHaveCount(3);
      await expectAccessible(page, testInfo, `agents-runs-${locale}`);
      await page.getByTestId("agents-runs").locator("tbody tr").first().getByRole("link").first().click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("agents.timeline.title"));
      await expect(page.getByTestId("agents-run-steps").locator("li").first()).toBeVisible();
      await expectAccessible(page, testInfo, `agents-run-${locale}`);

      // The kill switch stops every agent until it is switched back on.
      await page.goto(`${hostOrigin(tenant.key)}/admin/agents/policy`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("agents.policy.title"));
      await expectAccessible(page, testInfo, `agents-policy-${locale}`);
      const kill = page.getByTestId("agent-kill-switch");
      await expect(kill.getByTestId("agent-kill-switch-state")).toHaveText(m("agents.killSwitch.off"));
      await kill.getByLabel(m("agents.killSwitch.reason"), { exact: true }).fill(locale === "ar" ? "تمرين الطوارئ" : "Emergency drill");
      await kill.getByLabel(m("agents.killSwitch.confirmation"), { exact: true }).fill(`${tenant.key} holding`);
      await kill.getByRole("button", { name: m("agents.killSwitch.turnOn") }).click();
      await expect(kill.getByTestId("agent-kill-switch-state")).toHaveText(m("agents.killSwitch.on"));
      await page.goto(`${hostOrigin(tenant.key)}/agents`);
      await expect(page.getByText(m("agents.use.empty"))).toBeVisible();
      await page.goto(`${hostOrigin(tenant.key)}/admin/agents/policy`);
      await page.getByTestId("agent-kill-switch").getByRole("button", { name: m("agents.killSwitch.turnOff") }).click();
      await expect(page.getByTestId("agent-kill-switch-state")).toHaveText(m("agents.killSwitch.off"));
    });
  });
}
