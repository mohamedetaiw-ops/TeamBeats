import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 026 T016: the security pages. The administrator weakens a session rule and sees it on the posture page, then
 * handles an alert raised by a blocked network, in English and Arabic with axe. The API decides every rule.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`security pages in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`sec-${locale}`, locale);
    });

    test("an administrator changes the session rules and the posture page shows the weaker setting", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin`);
      await page.getByRole("navigation", { name: msg(locale, "admin.navLabel") }).getByRole("link", { name: msg(locale, "admin.nav.security") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "security.policy.title"));
      await expectAccessible(page, testInfo, `security-policy-${locale}`);

      // The lock-out preview for a range nobody signs in from.
      await page.getByLabel(msg(locale, "security.policy.network.block")).fill("192.0.2.0/24");
      await page.getByRole("button", { name: msg(locale, "security.policy.network.preview") }).click();
      // The count is a plural message; every form names the 30 day window.
      await expect(page.getByRole("status").filter({ hasText: "30" })).toBeVisible();

      await page.getByLabel(msg(locale, "security.policy.network.block")).fill("");
      await page.getByLabel(msg(locale, "security.policy.sessions.idle")).fill("120");
      await page.getByRole("button", { name: msg(locale, "security.policy.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "security.policy.saved") })).toBeVisible();

      await page.getByRole("link", { name: msg(locale, "security.tabs.posture") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "security.posture.title"));
      const idle = page.getByRole("listitem").filter({ has: page.getByRole("heading", { name: msg(locale, "security.posture.checks.idle_timeout.title") }) });
      await expect(idle.getByText(msg(locale, "security.posture.states.failed"))).toBeVisible();
      await expect(idle.getByRole("link", { name: msg(locale, "security.posture.fix") })).toHaveAttribute("href", "/admin/security");
      await expectAccessible(page, testInfo, `security-posture-${locale}`);
    });

    test("a blocked network raises an alert that the administrator handles with a note", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const policy = await api.get("/api/v1/security/policy");
      const saved = await api.put("/api/v1/security/policy", {
        data: { ipBlock: ["198.51.100.7/32"] },
        headers: { "if-match": policy.headers()["etag"] ?? "" },
      });
      expect(saved.ok()).toBe(true);
      const blocked = await api.get("/api/v1/me", { headers: { "x-forwarded-for": "198.51.100.7" } });
      expect(blocked.status()).toBe(403);
      await api.dispose();

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/security/alerts`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "security.alerts.title"));
      const alert = page.getByRole("listitem").filter({ has: page.getByRole("heading", { name: msg(locale, "security.alerts.kinds.network_blocked") }) });
      await expect(alert.getByText("198.51.100.7")).toBeVisible();
      await expectAccessible(page, testInfo, `security-alerts-${locale}`);

      await alert.getByLabel(msg(locale, "security.alerts.note")).fill(locale === "ar" ? "عنوان اختبار معروف" : "Known test address");
      await alert.getByRole("button", { name: msg(locale, "security.alerts.markHandled") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "security.alerts.done") })).toBeVisible();

      await page.getByLabel(msg(locale, "security.alerts.filter")).selectOption("handled");
      const handled = page.getByRole("listitem").filter({ has: page.getByRole("heading", { name: msg(locale, "security.alerts.kinds.network_blocked") }) });
      await expect(handled.getByText(msg(locale, "security.alerts.outcome.handled"))).toBeVisible();
    });
  });
}

/**
 * Spec 026 T020: SIEM streaming. In a package with the capability, the administrator adds a syslog destination for an
 * allowed host, sends a test event that cannot arrive (the host does not resolve), and sees why, in English and Arabic.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`SIEM streaming in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const host = "siem.example.test";

    test.beforeAll(async () => {
      tenant = await createTenant(`siem-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const allowed = await api.post("/api/v1/integrations/allowlist", { data: { host } });
      expect(allowed.ok()).toBe(true);
      await api.dispose();
    });

    test("an administrator adds a syslog destination and a failed test event says why", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/security`);
      await page.getByRole("link", { name: msg(locale, "security.tabs.siem"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "security.siem.title"));
      await expect(page.getByText(msg(locale, "security.siem.none"))).toBeVisible();
      await expectAccessible(page, testInfo, `security-siem-empty-${locale}`);

      await page.getByRole("radio", { name: msg(locale, "security.siem.transports.syslog") }).check();
      await page.getByLabel(msg(locale, "security.siem.name"), { exact: true }).fill(locale === "ar" ? "مركز الأمن" : "Security centre");
      await page.getByLabel(msg(locale, "security.siem.host"), { exact: true }).fill(host);
      await page.getByRole("button", { name: msg(locale, "security.siem.add") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "security.siem.added") })).toBeVisible();
      const row = page.getByRole("row").filter({ hasText: host });
      await expect(row).toBeVisible();

      await row.getByRole("button", { name: msg(locale, "security.siem.test") }).click();
      const prefix = msg(locale, "security.siem.testFailed").split("{error}")[0].trim();
      await expect(page.getByRole("status").or(page.getByRole("alert")).filter({ hasText: prefix })).toBeVisible();
      await expectAccessible(page, testInfo, `security-siem-${locale}`);
    });
  });
}

/**
 * Spec 026 T025: alert thresholds. In a package with advanced alerts, the administrator lowers the refused app call
 * threshold and sends guest Confidential alerts to the sponsor too; the values stay after a reload. English and Arabic.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`alert thresholds in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`thr-${locale}`, locale, "workplace_plus");
    });

    test("an administrator changes alert thresholds and they are kept", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/security/alerts`);
      await expect(page.getByRole("heading", { level: 2, name: msg(locale, "security.alerts.settings.title") })).toBeVisible();
      const denied = page.getByLabel(msg(locale, "security.alerts.settings.apiDenied"), { exact: true });
      await expect(denied).toHaveValue("50");

      await denied.fill("20");
      await page.getByLabel(msg(locale, "security.alerts.settings.guestNotify"), { exact: true }).selectOption("both");
      await page.getByRole("button", { name: msg(locale, "security.alerts.settings.save") }).click();
      await expect(page.getByRole("status").or(page.getByRole("alert")).filter({ hasText: msg(locale, "security.alerts.settings.saved") })).toBeVisible();
      await expectAccessible(page, testInfo, `security-thresholds-${locale}`);

      await page.reload();
      await expect(page.getByLabel(msg(locale, "security.alerts.settings.apiDenied"), { exact: true })).toHaveValue("20");
      await expect(page.getByLabel(msg(locale, "security.alerts.settings.guestNotify"), { exact: true })).toHaveValue("both");
    });
  });
}

/**
 * Spec 026 T027: sensitive data detection. The administrator turns detection on and adds a tested pattern; an item
 * whose body holds a made-up IBAN then warns on publish, and goes out once the author gives a reason. English and Arabic.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`sensitive data detection in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`sdd-${locale}`, locale, "workplace_plus");
    });

    test("an administrator turns detection on and adds a pattern tested on sample text", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/security`);
      await page.getByRole("link", { name: msg(locale, "security.tabs.detection"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "security.detection.title"));
      await expectAccessible(page, testInfo, `security-detection-${locale}`);

      await page.getByLabel(msg(locale, "security.detection.enabled"), { exact: true }).check();
      await page.getByRole("button", { name: msg(locale, "security.detection.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "security.detection.saved") })).toBeVisible();

      await page.getByLabel(msg(locale, "security.detection.name"), { exact: true }).fill(locale === "ar" ? "رقم الموظف" : "Employee number");
      await page.getByLabel(msg(locale, "security.detection.pattern"), { exact: true }).fill("\\bEMP-\\d{6}\\b");
      await page.getByLabel(msg(locale, "security.detection.sample"), { exact: true }).fill("EMP-004211");
      await page.getByRole("button", { name: msg(locale, "security.detection.test") }).click();
      await expect(page.getByRole("status").filter({ hasText: locale === "ar" ? "تطابقا واحدا" : "1 match" })).toBeVisible();
      await page.getByRole("button", { name: msg(locale, "security.detection.add") }).click();
      await expect(page.getByRole("row").filter({ hasText: "EMP-" })).toBeVisible();
      await expectAccessible(page, testInfo, `security-detection-pattern-${locale}`);
    });

    test("an author is warned about an IBAN on publish and confirms with a reason", async ({ page }) => {
      const api = await tenantApi(tenant);
      const me = await (await api.get("/api/v1/me")).json();
      const created = await api.post("/api/v1/content/items", {
        data: {
          type: "page",
          ownerId: me.id,
          variants: { [locale]: { title: locale === "ar" ? "بيانات المورد" : "Supplier details", body: { blocks: [{ type: "paragraph", children: [{ text: "IBAN GB82WEST12345698765432" }] }] } } },
        },
      });
      expect(created.status()).toBe(201);
      const item = await created.json();
      await api.dispose();

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/items/${item.id}`);
      await page.getByRole("button", { name: msg(locale, "content.items.publish") }).click();
      await expect(page.getByText(msg(locale, "security.sensitive.foundTitle"))).toBeVisible();
      await expect(page.getByRole("listitem").filter({ hasText: msg(locale, "security.sensitive.types.iban") })).toBeVisible();
      await page.getByLabel(msg(locale, "security.sensitive.reason"), { exact: true }).fill(locale === "ar" ? "بيانات منشورة للمورد" : "The supplier's published details.");
      await page.getByRole("button", { name: msg(locale, "security.sensitive.confirm") }).click();
      await expect(page.getByTestId("item-status")).toContainText(msg(locale, "content.items.status.published"));
    });
  });
}
