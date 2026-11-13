import type { Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 030 slice 1 (US1, US5): the installable app and phone layouts. On a 360 px phone the bottom navigation shows the
 * first entries with labels and "More" for the rest, mirrored in Arabic, with targets of at least 44 by 44; every R1 and
 * R2 page opens with no horizontal page scroll in both languages; the service worker installs, a new version waits for
 * Reload and never shows while a field has focus, and with no connection the offline page answers. The manifest per
 * tenant and brand, the icon and the navigation rules are covered by AppInstallTests and ShellNavigationTests.
 */
const phone = { width: 360, height: 740 };

/** Every page of R1 and R2 that opens without an id. Development tools, the gallery and the sign-in pages are left out. */
const routes = [
  "/", "/services", "/requests", "/requests/team", "/tasks", "/people", "/people/me", "/people/team", "/my-details", "/documents", "/kb",
  "/kb/new", "/search", "/notifications", "/notifications/preferences", "/settings", "/assistant", "/agents", "/calendar", "/calendar/manage",
  "/policies", "/policies/my", "/policies/new", "/content/items", "/content/items/new", "/content/health", "/leave", "/leave/team",
  "/attendance", "/attendance/team", "/letters", "/payslips", "/expenses", "/hr/journeys", "/communities", "/communities/awards",
  "/communities/celebrations", "/communities/moderation", "/communities/recognition", "/communities/surveys", "/learning",
  "/learning/catalogue", "/learning/manage", "/learning/reports", "/learning/team", "/spaces", "/spaces/manage", "/spaces/new", "/booking",
  "/booking/bookings", "/booking/manage", "/booking/reports", "/admin", "/admin/tenant", "/admin/users", "/admin/roles", "/admin/organization",
  "/admin/organization/directory", "/admin/identity", "/admin/plan", "/admin/branding", "/admin/shell", "/admin/pages", "/admin/pages/new",
  "/admin/templates", "/admin/people", "/admin/services", "/admin/notifications", "/admin/search", "/admin/connections",
  "/admin/connections/new", "/admin/calendars", "/admin/kiosks", "/admin/security", "/admin/security/alerts", "/admin/security/posture", "/admin/audit",
  "/admin/analytics", "/admin/analytics/errors", "/admin/ai", "/admin/ai/assistant", "/admin/ai/routing", "/admin/ai/usage", "/admin/agents",
  "/admin/agents/policy", "/admin/agents/runs", "/admin/evaluation", "/admin/evaluation/settings", "/admin/leave", "/admin/attendance",
  "/admin/letters", "/admin/expenses", "/admin/journeys", "/admin/data-changes", "/admin/hr-integration", "/learning/certificates/verify",
  "/letters/verify", "/offline", "/app-consent", "/admin/api", "/settings/connected-apps",
];

/** Groups of pages, one test each, so the suite spreads over the shards. */
const groups = Array.from({ length: Math.ceil(routes.length / 12) }, (_, i) => routes.slice(i * 12, i * 12 + 12));

/** Menu entries for the bottom navigation: six, so four show and "More" holds two. */
const entries = [
  { url: "/services", en: "Services", ar: "الخدمات" },
  { url: "/people", en: "People", ar: "الأشخاص" },
  { url: "/documents", en: "Documents", ar: "المستندات" },
  { url: "/booking", en: "Booking", ar: "الحجز" },
  { url: "/learning", en: "Learning", ar: "التعلم" },
  { url: "/policies", en: "Policies", ar: "السياسات" },
];

async function seedMenu(tenant: TestTenant) {
  const api = await tenantApi(tenant);
  const items = [];
  for (const entry of entries) {
    const id = crypto.randomUUID();
    const slug = `m-${entry.url.slice(1)}-${Math.random().toString(36).slice(2, 8)}`;
    const node = await api.put(`/api/v1/experience/ia/${id}`, { data: { labels: { en: entry.en, ar: entry.ar }, slug, url: entry.url } });
    expect(node.ok(), await node.text()).toBe(true);
    items.push({ nodeId: id });
  }
  const menu = await api.put("/api/v1/experience/menus/global/tenant", { data: { items } });
  expect(menu.ok(), await menu.text()).toBe(true);
  await api.dispose();
}

/** The page is no wider than the window: nothing makes the whole page scroll sideways (FR-003). */
async function overflow(page: Page): Promise<number> {
  return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

/**
 * Controls whose hit area is under 44 by 44px (DLS-085). A small text link may keep its look and take its area from an
 * absolutely placed ::after; a link inside running text is exempt, as in WCAG 2.5.8.
 */
async function smallTargets(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const out: string[] = [];
    for (const el of document.querySelectorAll<HTMLElement>("button, a[href], select, input:not([type=hidden]), textarea, summary, [role=tab]")) {
      const box = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      if (box.width === 0 || box.height === 0 || style.visibility === "hidden") continue;
      if (style.display === "inline" && el.closest("p")) continue;
      const after = getComputedStyle(el, "::after");
      const extra = after.content !== "none" && after.position === "absolute";
      const width = Math.max(box.width, extra ? parseFloat(after.width) || 0 : 0);
      const height = Math.max(box.height, extra ? parseFloat(after.height) || 0 : 0);
      if (width < 43.5 || height < 43.5) {
        const name = (el.getAttribute("aria-label") ?? el.textContent ?? "").trim().slice(0, 40);
        out.push(`${el.tagName.toLowerCase()}${el.className ? "." + String(el.className).split(" ").join(".") : ""} "${name}" ${Math.round(width)}x${Math.round(height)}`);
      }
    }
    return out;
  });
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`mobile app in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string) => msg(locale, key);

    test.beforeAll(async () => {
      tenant = await createTenant(`mob-${locale}`, locale, "workplace_plus");
      await seedMenu(tenant);
    });

    test("the bottom navigation shows four entries and More, mirrored and easy to tap", async ({ page }, testInfo) => {
      await page.setViewportSize(phone);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const nav = page.getByRole("navigation", { name: m("app.nav.label"), exact: true });
      await expect(nav).toBeVisible();
      const bar = nav.locator(".mobile-nav-bar > li");
      await expect(bar).toHaveCount(5);
      for (const entry of entries.slice(0, 4)) {
        await expect(nav.getByRole("link", { name: entry[locale], exact: true })).toBeVisible();
      }
      for (const target of await nav.locator(".mobile-nav-bar a, .mobile-nav-bar button").all()) {
        const box = (await target.boundingBox())!;
        expect(box.width).toBeGreaterThanOrEqual(44);
        expect(box.height).toBeGreaterThanOrEqual(44);
      }
      // The first entry sits at the start edge: the left in English and the right in Arabic.
      const first = (await nav.getByRole("link", { name: entries[0]![locale], exact: true }).boundingBox())!;
      if (locale === "ar") expect(first.x + first.width).toBeGreaterThan(phone.width - 80);
      else expect(first.x).toBeLessThan(80);

      const more = nav.getByRole("button", { name: m("app.nav.more"), exact: true });
      await expect(more).toHaveAttribute("aria-expanded", "false");
      await more.click();
      await expect(more).toHaveAttribute("aria-expanded", "true");
      for (const entry of entries.slice(4)) {
        await expect(nav.getByRole("link", { name: entry[locale], exact: true })).toBeVisible();
      }
      await expectAccessible(page, testInfo, `bottom navigation ${locale}`);
      await page.keyboard.press("Escape");
      await expect(more).toHaveAttribute("aria-expanded", "false");
      await expect(more).toBeFocused();

      await nav.getByRole("link", { name: entries[1]![locale], exact: true }).click();
      await expect(page).toHaveURL(/\/people$/);
      await expect(nav.getByRole("link", { name: entries[1]![locale], exact: true })).toHaveAttribute("aria-current", "page");
    });

    test("every control on Home, Services, Tasks and People has a 44 px hit area at 390 px", async ({ page }) => {
      await page.setViewportSize({ width: 390, height: 844 });
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const small: string[] = [];
      for (const route of ["/", "/services", "/tasks", "/people"]) {
        await page.goto(`${hostOrigin(tenant.key)}${route}`);
        await expect(page.locator("main").first()).toBeVisible();
        await page.waitForLoadState("networkidle");
        small.push(...(await smallTargets(page)).map((s) => `${route}: ${s}`));
      }
      expect(small, "controls under 44px").toEqual([]);
    });

    groups.forEach((group, i) => {
      test(`pages ${i + 1} of ${groups.length} fit a 360 px phone`, async ({ page }) => {
        test.slow();
        await page.setViewportSize(phone);
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        const wide: string[] = [];
        for (const route of group) {
          await page.goto(`${hostOrigin(tenant.key)}${route}`);
          await expect(page.locator("main").first()).toBeVisible();
          await page.waitForLoadState("load");
          await page.evaluate(() => document.fonts.ready);
          const extra = await overflow(page);
          if (extra > 1) wide.push(`${route} (${extra}px)`);
        }
        expect(wide, "pages wider than the phone").toEqual([]);
      });
    });
  });
}

test.describe("installed app", () => {
  let tenant: TestTenant;
  test.beforeAll(async () => {
    tenant = await createTenant("sw-en", "en", "workplace_plus");
    await seedMenu(tenant);
  });

  test("the experience administrator chooses the bottom navigation entries", async ({ page }) => {
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.goto(`${hostOrigin(tenant.key)}/admin/shell`);
    const section = page.getByRole("region", { name: msg("en", "shell_settings.mobile.title") });
    await expect(section.getByText(msg("en", "shell_settings.mobile.default"))).toBeVisible();
    await section.getByLabel("Policies", { exact: true }).check();
    await section.getByLabel("People", { exact: true }).check();
    await page.getByRole("button", { name: msg("en", "shell_settings.save"), exact: true }).click();
    await expect(page.getByText(msg("en", "shell_settings.saved"), { exact: true })).toBeVisible();

    await page.setViewportSize(phone);
    await page.goto(`${hostOrigin(tenant.key)}/`);
    const bar = page.getByRole("navigation", { name: msg("en", "app.nav.label"), exact: true }).locator(".mobile-nav-bar");
    // In the order of the global menu, then More for the other four.
    await expect(bar.getByRole("link")).toHaveText(["People", "Policies"]);
    await expect(bar.getByRole("button", { name: msg("en", "app.nav.more"), exact: true })).toBeVisible();
  });

  test("a new version waits for Reload, never shows while a field has focus or on a fresh page", async ({ page }) => {
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.evaluate(async () => {
      await navigator.serviceWorker.ready;
      if (!navigator.serviceWorker.controller) await new Promise((r) => navigator.serviceWorker.addEventListener("controllerchange", r, { once: true }));
    });
    const prompt = page.getByRole("status", { name: msg("en", "app.update.title") });
    await expect(prompt).toHaveCount(0);

    // The person is typing when the next release arrives.
    const search = page.locator("input[type=search]").first();
    await search.click();
    await search.fill("leave");
    await page.evaluate(() => navigator.serviceWorker.register("/sw.js?v=e2e-next", { scope: "/" }));
    await page.waitForFunction(async () => !!(await navigator.serviceWorker.getRegistration())?.waiting);
    await expect(prompt).toHaveCount(0);

    await search.blur();
    await expect(prompt).toBeVisible();
    await prompt.getByRole("button", { name: msg("en", "app.update.later"), exact: true }).click();
    await expect(prompt).toHaveCount(0);
    // A freshly loaded page is the current release: it takes over silently and offers nothing (TB-02).
    await page.goto(`${hostOrigin(tenant.key)}/people`);
    // While no page is open the waiting worker may take over on its own, so wait until this page's own release ("dev")
    // controls it and nothing is installing; otherwise its registration could replace the next one mid-test.
    await page.waitForFunction(async () => {
      const r = await navigator.serviceWorker.getRegistration();
      return !!r && !r.waiting && !r.installing && !!navigator.serviceWorker.controller?.scriptURL.includes("v=dev");
    });
    await expect(prompt).toHaveCount(0);
    // The release after that is offered again, and Reload applies it and reloads the page.
    await page.evaluate(() => navigator.serviceWorker.register("/sw.js?v=e2e-third", { scope: "/" }));
    await expect(prompt).toBeVisible();
    // The worker that takes control on Reload is noted before the page reloads; after it, this test page (release
    // "dev") installs its own worker again, which a real newer page would not.
    await page.evaluate(() =>
      navigator.serviceWorker.addEventListener("controllerchange", () => sessionStorage.setItem("e2e.controller", navigator.serviceWorker.controller?.scriptURL ?? ""), { once: true }),
    );
    await Promise.all([page.waitForEvent("load"), prompt.getByRole("button", { name: msg("en", "app.update.reload"), exact: true }).click()]);
    expect(await page.evaluate(() => sessionStorage.getItem("e2e.controller") ?? "")).toContain("v=e2e-third");
    await expect(prompt).toHaveCount(0);
  });

  test("with no connection a page opens the offline page", async ({ page, context }) => {
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.evaluate(async () => {
      await navigator.serviceWorker.ready;
      if (!navigator.serviceWorker.controller) await new Promise((r) => navigator.serviceWorker.addEventListener("controllerchange", r, { once: true }));
    });
    await context.setOffline(true);
    await page.goto(`${hostOrigin(tenant.key)}/people`).catch(() => undefined);
    await expect(page.getByRole("main").getByText(msg("en", "app.offline.title"), { exact: true })).toBeVisible();
    await context.setOffline(false);
  });
});

/**
 * Spec 030 slice 2 (US2): push on this device. The offer appears only after a meaningful action and "Not now" keeps it
 * away for 90 days; the preferences page turns push on and off for the device and per category. The browser's push
 * service is replaced with a stand-in subscription on a known push host, because the test browser has none; the
 * encryption, signing and delivery are covered by PushTests.
 */
test.describe("push on this device", () => {
  let tenant: TestTenant;
  test.beforeAll(async () => {
    tenant = await createTenant("push-en", "en", "workplace_plus");
  });

  test("the offer waits for a meaningful action and Not now is remembered", async ({ page }) => {
    // A headless browser answers "denied" without ever asking; a real one starts at "default", not asked yet.
    await page.addInitScript(() => Object.defineProperty(Notification, "permission", { get: () => "default" }));
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    const offer = page.getByRole("region", { name: msg("en", "app.push.title"), exact: true });
    expect(await page.evaluate(() => Notification.permission)).toBe("default");
    // Never on a first visit.
    await expect(offer).toHaveCount(0);

    await page.evaluate(() => window.dispatchEvent(new CustomEvent("tb:meaningful-action")));
    await expect(offer).toBeVisible();
    await offer.getByRole("button", { name: msg("en", "app.push.notNow"), exact: true }).click();
    await expect(offer).toHaveCount(0);

    await page.reload();
    await page.evaluate(() => window.dispatchEvent(new CustomEvent("tb:meaningful-action")));
    await page.waitForTimeout(500);
    await expect(offer).toHaveCount(0);
    const declined = await page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith("tb:push-declined:")).length);
    expect(declined).toBe(1);
  });

  test("the preferences page turns push on and off for this device and per category", async ({ page }) => {
    await page.addInitScript(() => {
      // The person allows notifications when the browser asks; a headless browser would answer "denied" on its own.
      let permission: NotificationPermission = "default";
      Object.defineProperty(Notification, "permission", { get: () => permission });
      Notification.requestPermission = async () => (permission = "granted");
      let current: PushSubscription | null = null;
      const b64 = (bytes: ArrayBuffer | Uint8Array) =>
        btoa(String.fromCharCode(...new Uint8Array(bytes))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
      PushManager.prototype.subscribe = async function (options?: PushSubscriptionOptionsInit) {
        const pair = (await crypto.subtle.generateKey({ name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"])) as CryptoKeyPair;
        const p256dh = b64(await crypto.subtle.exportKey("raw", pair.publicKey));
        const auth = b64(crypto.getRandomValues(new Uint8Array(16)));
        const endpoint = `https://fcm.googleapis.com/fcm/send/e2e-${crypto.randomUUID()}`;
        const key = options?.applicationServerKey;
        current = {
          endpoint,
          expirationTime: null,
          options: { userVisibleOnly: true, applicationServerKey: key instanceof Uint8Array ? key.buffer : (key as ArrayBuffer) ?? null },
          getKey: () => null,
          toJSON: () => ({ endpoint, keys: { p256dh, auth } }),
          unsubscribe: async () => {
            current = null;
            return true;
          },
        } as unknown as PushSubscription;
        return current;
      };
      PushManager.prototype.getSubscription = async () => current;
    });
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.goto(`${hostOrigin(tenant.key)}/notifications/preferences`);
    await page.evaluate(() => navigator.serviceWorker.ready);
    const m = (key: string) => msg("en", `notifications.preferences.${key}`);
    await expect(page.getByText(m("pushIsOff"), { exact: true })).toBeVisible();
    await page.getByRole("button", { name: m("pushTurnOn"), exact: true }).click();
    await expect(page.getByRole("status").getByText(m("pushTurnedOn"), { exact: true })).toBeVisible();
    await expect(page.getByText(m("pushIsOn"), { exact: true })).toBeVisible();
    const devices = () => page.evaluate(async () => ((await (await fetch("/api/v1/me/push")).json()) as { devices: number }).devices);
    expect(await devices()).toBe(1);
    await page.getByRole("button", { name: m("pushTurnOff"), exact: true }).click();
    await expect(page.getByRole("status").getByText(m("pushTurnedOff"), { exact: true })).toBeVisible();
    expect(await devices()).toBe(0);

    // One category without push, kept after saving.
    const category = page.locator("tr[data-category]").filter({ has: page.locator("input:not([disabled])") }).first();
    const box = category.getByRole("checkbox", { name: /^Push for / });
    await expect(box).toBeChecked();
    await box.uncheck();
    await page.getByRole("button", { name: m("save"), exact: true }).click();
    await expect(page.getByRole("status").getByText(m("saved"), { exact: true })).toBeVisible();
    await page.reload();
    await expect(page.locator("tr[data-category]").filter({ has: page.locator("input:not([disabled])") }).first().getByRole("checkbox", { name: /^Push for / })).not.toBeChecked();

  });
});

/**
 * Spec 030 slice 3 (US3): offline reading. While online the app keeps the critical announcement targeted to the person,
 * encrypted; with no connection the offline page lists it and opens it under the "offline, last updated" banner; signing
 * out deletes the cache. Eligibility, limits, pruning and the key are covered by OfflineTests.
 */
test.describe("offline reading", () => {
  let tenant: TestTenant;
  const title = "Gas leak drill on Monday";
  const body = "Meet at the north gate at 9.";

  test.beforeAll(async () => {
    tenant = await createTenant("off-en", "en", "workplace_plus");
    const api = await tenantApi(tenant);
    const created = await api.post("/api/v1/content/items", {
      data: { type: "announcement", variants: { en: { title, body: { blocks: [{ type: "paragraph", children: [{ text: body }] }] } } }, critical: { presentation: "banner" } },
    });
    expect(created.status(), await created.text()).toBe(201);
    const id = ((await created.json()) as { id: string }).id;
    expect((await api.post(`/api/v1/content/items/${id}/publish`, { data: { material: true } })).ok()).toBe(true);
    // The audience step runs in the background; wait until the item is the administrator's.
    await expect
      .poll(async () => ((await (await api.get("/api/v1/me/offline")).json()) as { items: { id: string }[] }).items.some((i) => i.id === id))
      .toBe(true);
    await api.dispose();
  });

  const kept = (page: Page) =>
    page.evaluate(
      () =>
        new Promise<number>((resolve) => {
          const request = indexedDB.open("tb-offline", 1);
          request.onupgradeneeded = () => request.transaction?.abort();
          request.onerror = () => resolve(0);
          request.onsuccess = () => {
            const db = request.result;
            if (!db.objectStoreNames.contains("cache")) return resolve(0);
            const get = db.transaction("cache").objectStore("cache").get("current");
            get.onsuccess = () => {
              const value = get.result as { items: { data: ArrayBuffer }[] } | undefined;
              // The stored item is ciphertext: its bytes never contain the title.
              const plain = value?.items.some((i) => new TextDecoder().decode(i.data).includes("Gas leak"));
              db.close();
              resolve(plain ? -1 : (value?.items.length ?? 0));
            };
          };
        }),
    );

  test("a kept announcement opens with no connection under the offline banner, and sign-out deletes it", async ({ page, context }, testInfo) => {
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.evaluate(async () => {
      await navigator.serviceWorker.ready;
      if (!navigator.serviceWorker.controller) await new Promise((r) => navigator.serviceWorker.addEventListener("controllerchange", r, { once: true }));
    });
    await expect.poll(() => kept(page)).toBe(1);

    await context.setOffline(true);
    await page.goto(`${hostOrigin(tenant.key)}/people`).catch(() => undefined);
    const library = page.getByRole("region", { name: msg("en", "app.offline.library.title"), exact: true });
    await expect(library).toBeVisible();
    await expect(library.getByText(/^Offline\. Last updated /)).toBeVisible();
    await library.getByRole("button", { name: new RegExp(title) }).click();
    await expect(page.getByRole("heading", { name: title, level: 2 })).toBeFocused();
    await expect(page.getByText(body, { exact: true })).toBeVisible();
    await expect(page.getByText(/^Offline\. Last updated /)).toBeVisible();
    await expectAccessible(page, testInfo, "offline reader");
    await page.getByRole("button", { name: msg("en", "app.offline.library.back"), exact: true }).click();
    await expect(library).toBeVisible();
    await context.setOffline(false);

    await page.goto(`${hostOrigin(tenant.key)}/`);
    await page.getByRole("button", { name: /^Account/ }).click();
    await page.getByRole("button", { name: msg("en", "shell.signOut"), exact: true }).click();
    await page.waitForURL(/\/signin/);
    await expect.poll(() => kept(page)).toBe(0);
  });
});
