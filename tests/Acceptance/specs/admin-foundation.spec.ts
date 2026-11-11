import type { Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * The R0 administration screens (specs 001 to 006): workspace settings, organization, people, roles and access, plan,
 * sign-in settings and the audit log, in English and Arabic with axe. Every section is opened from the admin
 * navigation the API returns. The roles journey also removes an administrator's role and checks that the person then
 * has no way into administration at all: the API decides, the screens follow.
 */
async function openSection(page: Page, tenantKey: string, locale: LaunchLocale, section: string) {
  await page.goto(`${hostOrigin(tenantKey)}/admin`);
  await page.getByRole("navigation", { name: msg(locale, "admin.navLabel") }).getByRole("link", { name: msg(locale, `admin.nav.${section}`), exact: true }).click();
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, `admin.nav.${section}`));
}

const saved = (page: Page, locale: LaunchLocale, key: string) => page.getByRole("status").filter({ hasText: msg(locale, key) });

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`administration screens in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    const m = (key: string, values?: Record<string, string>) => msg(locale, key, values);

    test("an administrator changes workspace settings, appoints an administrator and reads the plan", async ({ page }, testInfo) => {
      const tenant = await createTenant(`ws-${locale}`, locale);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);

      await openSection(page, tenant.key, locale, "tenant_settings");
      await expect(page.getByText(m("admin.tenant.workspace.nameHint"))).toBeVisible();
      await expectAccessible(page, testInfo, `admin-tenant-${locale}`);

      await page.getByLabel(m("admin.tenant.language.arab")).check();
      await page.getByRole("button", { name: m("admin.tenant.save") }).click();
      await expect(saved(page, locale, "admin.tenant.saved")).toBeVisible();
      await page.reload();
      await expect(page.getByLabel(m("admin.tenant.language.arab"))).toBeChecked();

      const admins = page.getByRole("region", { name: m("admin.tenant.admins.title") });
      await admins.getByLabel(m("admin.tenant.admins.newEmail")).fill(`sara@${tenant.key}.example`);
      await admins.getByRole("button", { name: m("admin.tenant.admins.appoint"), exact: true }).click();
      await expect(saved(page, locale, "admin.tenant.admins.appointed")).toBeVisible();
      await expect(admins.getByRole("row").filter({ hasText: `sara@${tenant.key}.example` })).toContainText(m("admin.tenant.admins.statuses.pending"));

      await openSection(page, tenant.key, locale, "plan");
      await expect(page.getByText(m("admin.plan.packages.workplace"))).toBeVisible();
      await expect(page.locator("#main").getByText(m("admin.plan.capabilities.people_directory"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, `admin-plan-${locale}`);
      await page.getByRole("radio", { name: m("admin.plan.filters.missing") }).check();
      await expect(page.locator("#main").getByText(m("admin.plan.capabilities.people_directory"), { exact: true })).toHaveCount(0);
    });

    test("an administrator builds part of the organization and adds a member", async ({ page }, testInfo) => {
      const tenant = await createTenant(`org-${locale}`, locale);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await openSection(page, tenant.key, locale, "organization");

      const name = locale === "ar" ? "مجموعة الاختبار" : "Test Group";
      const child = locale === "ar" ? "إدارة المالية" : "Finance";
      await page.getByRole("button", { name: m("admin.org.tree.addTop") }).click();
      const dialog = page.getByRole("dialog", { name: m("admin.org.form.createTop") });
      await dialog.getByLabel(m("admin.org.unit.name_en")).fill(locale === "ar" ? "Test Group" : name);
      await dialog.getByLabel(m("admin.org.unit.name_ar")).fill(locale === "ar" ? name : "مجموعة الاختبار");
      await dialog.getByLabel(m("admin.org.unit.code")).fill("GRP");
      await dialog.getByLabel(m("admin.org.unit.type")).selectOption("organization");
      await expectAccessible(page, testInfo, `admin-org-form-${locale}`);
      await dialog.getByRole("button", { name: m("admin.org.form.create") }).click();
      await expect(saved(page, locale, "admin.org.form.created")).toBeVisible();
      await expect(page.getByRole("treeitem", { name })).toBeVisible();

      await page.getByRole("button", { name: m("admin.org.actions.addChild") }).click();
      const under = page.getByRole("dialog");
      await under.getByLabel(m(`admin.org.unit.name_${locale}`)).fill(child);
      await under.getByLabel(m("admin.org.unit.code")).fill("FIN");
      await under.getByRole("button", { name: m("admin.org.form.create") }).click();
      await expect(saved(page, locale, "admin.org.form.created")).toBeVisible();
      await expect(page.getByRole("heading", { level: 2, name: child })).toBeVisible();
      await expect(page.getByRole("treeitem", { name: child })).toBeVisible();
      await expect(page.getByText(m("admin.org.members.empty"))).toBeVisible();

      await page.getByRole("button", { name: m("admin.org.members.add") }).click();
      const add = page.getByRole("dialog");
      await add.getByLabel(m("services.person.search")).fill("admin");
      await add.getByRole("button", { name: m("services.person.find") }).click();
      await add.getByRole("list", { name: m("services.person.results") }).getByRole("button").first().click();
      await add.getByRole("button", { name: m("admin.org.members.addSubmit") }).click();
      await expect(saved(page, locale, "admin.org.members.added")).toBeVisible();
      await expect(page.getByRole("table", { name: m("admin.org.members.caption", { name: child }) }).getByRole("row")).toHaveCount(2);
      await expectAccessible(page, testInfo, `admin-org-${locale}`);
    });

    test("an administrator reviews people and removes a role, which closes administration for that person", async ({ page }, testInfo) => {
      const tenant = await createTenant(`ppl-${locale}`, locale, "workplace", ["rana"]);
      const rana = `rana@${tenant.key}.example`;
      // Rana's first sign-in turns her appointment into an active administrator role.
      await signInWithDevForm(page, tenant.key, rana, locale);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);

      await openSection(page, tenant.key, locale, "users");
      await expect(page.getByRole("link", { name: rana })).toBeVisible();
      await expectAccessible(page, testInfo, `admin-users-${locale}`);
      await page.getByRole("link", { name: rana }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(rana);
      await expect(page.getByRole("table", { name: m("admin.users.roles") })).toContainText(m("admin.roles.names.tenant_administrator"));
      await expectAccessible(page, testInfo, `admin-user-${locale}`);

      await page.getByRole("link", { name: m("admin.users.manageRoles") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("admin.nav.roles"));
      const row = page.getByRole("row").filter({ hasText: rana });
      await expect(row).toContainText(m("admin.roles.names.tenant_administrator"));
      await expectAccessible(page, testInfo, `admin-roles-${locale}`);
      await row.getByRole("button", { name: m("admin.roles.revoke") }).click();
      await row.getByRole("button", { name: m("admin.roles.revokeYes") }).click();
      await expect(saved(page, locale, "admin.roles.revoked")).toBeVisible();
      await expect(page.getByRole("row").filter({ hasText: rana })).toHaveCount(0);

      await page.getByRole("tab", { name: m("admin.roles.rolesTitle") }).click();
      await expect(page.getByRole("heading", { level: 3, name: new RegExp(m("admin.roles.names.security_officer")) })).toBeVisible();

      // Disabling yourself is refused by the API, and the screen says so.
      await page.goto(`${hostOrigin(tenant.key)}/admin/users`);
      await page.getByRole("link", { name: tenant.adminEmail }).click();
      await page.getByRole("button", { name: m("admin.users.disable") }).click();
      await page.getByRole("button", { name: m("admin.users.disableYes") }).click();
      await expect(page.getByRole("alert").filter({ hasText: m("admin.common.notDone") })).toBeVisible();

      // Without the role, Rana has no administration section, so every admin address sends her home.
      await signInWithDevForm(page, tenant.key, rana, locale);
      for (const path of ["/admin", "/admin/users", "/admin/roles", "/admin/audit"]) {
        await page.goto(`${hostOrigin(tenant.key)}${path}`);
        await expect(page).toHaveURL(`${hostOrigin(tenant.key)}/`);
      }
    });

    test("an administrator reads sign-in settings and the audit log", async ({ page }, testInfo) => {
      const tenant = await createTenant(`aud-${locale}`, locale);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);

      await openSection(page, tenant.key, locale, "identity");
      await expect(page.getByRole("region", { name: m("admin.identity.sso.title") }).getByText(m("admin.identity.sso.notSet"), { exact: true })).toBeVisible();
      await expect(page.getByText(m("admin.identity.directory.noConnection"))).toBeVisible();
      await expectAccessible(page, testInfo, `admin-identity-${locale}`);
      await page.getByLabel(m("admin.identity.sso.issuer")).fill("https://login.microsoftonline.com/common/v2.0");
      await page.getByLabel(m("admin.identity.sso.clientId")).fill("not-a-client-id");
      await page.getByLabel(m("admin.identity.sso.secretRef")).fill("plain-secret");
      await page.getByRole("button", { name: m("admin.identity.sso.save") }).click();
      await expect(page.getByRole("alert").filter({ hasText: m("admin.common.notDone") })).toBeVisible();

      await openSection(page, tenant.key, locale, "audit");
      const table = page.getByRole("table", { name: m("admin.audit.events") });
      await expect(table.getByRole("row").nth(1)).toBeVisible();
      await expectAccessible(page, testInfo, `admin-audit-${locale}`);
      await table.getByRole("button", { name: m("admin.audit.view") }).first().click();
      const entry = page.getByRole("dialog", { name: m("admin.audit.entry") });
      await expect(entry.getByText(m("admin.audit.changes"))).toBeVisible();
      await page.keyboard.press("Escape");

      await page.getByLabel(m("admin.audit.action")).fill("nothing.matches");
      await page.getByRole("button", { name: m("admin.audit.apply") }).click();
      await expect(page.getByText(m("admin.audit.emptyFiltered"))).toBeVisible();
      await page.getByRole("button", { name: m("admin.audit.clear") }).click();
      await expect(table.getByRole("row").nth(1)).toBeVisible();

      await page.getByRole("button", { name: m("admin.audit.integrity.check") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("admin.audit.integrity.intact") })).toBeVisible();

      await page.getByRole("button", { name: m("admin.audit.export.start") }).click();
      await expect(saved(page, locale, "admin.audit.export.started")).toBeVisible();
      await expect(page.getByRole("link", { name: m("admin.audit.export.download") })).toBeVisible({ timeout: 30_000 });
    });
  });
}
