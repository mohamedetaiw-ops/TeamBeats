import type { Page } from "@playwright/test";
import { expect, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Every admin page reads as the same kind of page (spec 006 FR-020 to FR-022, spec 036 DLS-092 to DLS-099). The check
 * walks every section the API lists for an administrator, in English and Arabic, at desktop and phone widths, so a new
 * admin page that skips the shared header, shows a stored key, leaves a table cell empty or uses tiny text fails here
 * instead of in a review.
 */
type Finding = { rule: string; detail: string };

/** Runs in the page: what the layout rules see on the current admin page. */
async function inspect(page: Page): Promise<{ h1: string[]; current: string[]; findings: Finding[]; overflow: boolean }> {
  return page.evaluate(() => {
    const visible = (el: Element) => {
      const r = el.getBoundingClientRect();
      const s = getComputedStyle(el);
      return r.width > 0 && r.height > 0 && s.visibility !== "hidden" && s.display !== "none" && !el.closest(".visually-hidden, [hidden]");
    };
    const findings: { rule: string; detail: string }[] = [];
    const main = document.querySelector("main#main") ?? document.body;
    const h1 = [...main.querySelectorAll("h1")].filter(visible).map((h) => (h.textContent ?? "").trim());
    const current = [...document.querySelectorAll(".admin-nav [aria-current='page']")].map((a) => (a.querySelector("span")?.textContent ?? "").trim());

    // DLS-092: no section heading repeats the page title.
    for (const h of main.querySelectorAll("h2, h3")) {
      if (visible(h) && h1.includes((h.textContent ?? "").trim())) findings.push({ rule: "repeated title", detail: (h.textContent ?? "").trim() });
    }

    // DLS-093: a stored key (area.name) is never the text people read, except as the secondary key or other code.
    const key = /^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$/;
    const leaves = [...main.querySelectorAll("*")].filter(
      (el) => !el.closest("code, pre, kbd, samp, input, textarea, select, script, style") && [...el.childNodes].some((n) => n.nodeType === 3 && (n.textContent ?? "").trim()),
    );
    for (const el of leaves) {
      const text = [...el.childNodes]
        .filter((n) => n.nodeType === 3)
        .map((n) => (n.textContent ?? "").trim())
        .join(" ")
        .trim();
      if (key.test(text) && !/\.(com|net|org|io|dev|example|test|invalid|local)$/.test(text) && visible(el)) findings.push({ rule: "stored key", detail: text });
      // Minimum text size: 12px for any text on the page.
      const size = parseFloat(getComputedStyle(el).fontSize);
      if (size < 12 && visible(el)) findings.push({ rule: "text under 12px", detail: `${text.slice(0, 40)} (${size}px)` });
    }

    // DLS-095: a table cell is never blank.
    for (const td of main.querySelectorAll("td")) {
      if (visible(td) && !(td.textContent ?? "").trim() && !td.querySelector("input, select, textarea, button, a, svg, img, [role]")) {
        findings.push({ rule: "empty cell", detail: (td.closest("table")?.getAttribute("data-testid") ?? td.closest("table")?.querySelector("caption")?.textContent ?? "table").trim() });
      }
    }

    // A visible table caption that repeats the heading right above it says the same thing twice.
    for (const caption of main.querySelectorAll("caption")) {
      const heading = caption.closest("section")?.querySelector("h2, h3");
      if (visible(caption) && heading && (heading.textContent ?? "").trim() === (caption.textContent ?? "").trim()) {
        findings.push({ rule: "caption repeats heading", detail: (caption.textContent ?? "").trim() });
      }
    }

    return { h1, current, findings, overflow: document.documentElement.scrollWidth > window.innerWidth + 1 };
  });
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`admin page layout in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });

    test("every admin section has its menu item marked, one title equal to it, no keys, no blank cells and no small text", async ({ page }) => {
      test.setTimeout(600_000);
      const tenant = await createTenant(`adminlayout-${locale}`, locale, "workplace_plus");
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const origin = hostOrigin(tenant.key);
      const nav = await page.request.get(`${origin}/api/v1/admin/navigation`);
      expect(nav.ok()).toBe(true);
      const sections = (await nav.json()) as { key: string; path: string; labelKey: string }[];
      expect(sections.length).toBeGreaterThan(5);

      // Menu names never repeat (spec 006 FR-020): two items with one name cannot both be right.
      const names = sections.map((s) => msg(locale, s.labelKey));
      expect(new Set(names).size, "duplicate admin menu names").toBe(names.length);

      const problems: string[] = [];
      for (const width of [1280, 390]) {
        await page.setViewportSize({ width, height: 900 });
        for (const section of sections) {
          await page.goto(`${origin}${section.path}`);
          await expect(page.locator("main h1").first()).toBeVisible();
          const seen = await inspect(page);
          const where = `${section.path} at ${width}px`;
          const name = msg(locale, section.labelKey);
          if (seen.h1.length !== 1) problems.push(`${where}: ${seen.h1.length} h1 elements`);
          if (width > 1024 && (seen.current.length !== 1 || seen.current[0] !== name)) problems.push(`${where}: current menu item ${JSON.stringify(seen.current)}, expected ${name}`);
          // The section's own page (not a tab or record inside it) is titled with its menu name.
          if (new URL(page.url()).pathname === section.path && seen.h1[0] !== name) problems.push(`${where}: title "${seen.h1[0]}" differs from menu name "${name}"`);
          if (width === 390 && seen.overflow) problems.push(`${where}: the page scrolls sideways`);
          for (const f of seen.findings) problems.push(`${where}: ${f.rule}: ${f.detail}`);
        }
      }
      expect(problems, problems.join("\n")).toEqual([]);
    });
  });
}
