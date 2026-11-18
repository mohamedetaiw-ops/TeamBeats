import type { Page } from "@playwright/test";
import { expect, signInWithDevForm, test } from "../support/fixtures";
import { settle } from "../support/a11y";
import { seedHome } from "../support/home";
import type { LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";
import { routes } from "./routes";

/**
 * Real-page checks for what axe cannot see (spec 038 US2 and US3), on the primary pages in Beats (rail navigation) and
 * Studio (sidebar navigation, compact density), in Arabic and English:
 *  - A11Y-009 skip link first, visible focus on every Tab stop, no positive tabindex;
 *  - A11Y-010 no Tab stop entirely hidden by a fixed or sticky element (WCAG 2.4.11);
 *  - A11Y-011 targets of at least 24 by 24 CSS pixels (WCAG 2.5.8);
 *  - A11Y-012 reflow at 320 CSS pixels, A11Y-013 text at 200 percent, A11Y-014 text spacing, A11Y-015 reduced motion.
 * Findings are soft, so one run lists every page that fails.
 */
const designs = ["beats", "studio"] as const;
const locales = ["en", "ar"] as const satisfies readonly LaunchLocale[];
const primary = routes.filter((r) => r.tier === "primary" && !r.public).map((r) => r.path);
const tabStops = 40;

async function setup(tenant: TestTenant, design: string, values: Record<string, string>) {
  const api = await tenantApi(tenant);
  try {
    const brands = (await (await api.get("/api/v1/experience/brands")).json()) as { id: string; name: string }[];
    expect((await api.put(`/api/v1/experience/brands/${brands[0]!.id}`, { data: { name: brands[0]!.name, tokens: { "design.preset": { $value: design } } } })).ok()).toBe(true);
    expect((await api.put("/api/v1/me/preference-profile", { data: { values } })).ok()).toBe(true);
  } finally {
    await api.dispose();
  }
}

/** Facts about the focused element: its focus indicator, whether fixed or sticky parts cover it entirely. */
function focusFacts(page: Page) {
  return page.evaluate(() => {
    const el = document.activeElement as HTMLElement | null;
    // The development server's own overlay is not part of the product, and it comes last in the Tab order.
    if (!el || el === document.body || el.closest("nextjs-portal") || el.tagName.toLowerCase() === "nextjs-portal") return null;
    const ring = (node: Element | null) => {
      if (!node) return false;
      const s = getComputedStyle(node);
      const outline = s.outlineStyle !== "none" && parseFloat(s.outlineWidth) >= 2;
      const shadow = s.boxShadow !== "none" && s.boxShadow !== "";
      return outline || shadow;
    };
    // Some parts draw the ring on a wrapper or on the visible mark next to a hidden native input.
    // A card with a cover link draws the ring on the card (:has(:focus-visible)), up to a few levels above the link.
    let indicated = ring(el.nextElementSibling);
    for (let node: Element | null = el, level = 0; node && level < 5 && !indicated; node = node.parentElement, level++) indicated = ring(node);
    const box = el.getBoundingClientRect();
    const covering = (x: number, y: number) => {
      if (x < 0 || y < 0 || x > innerWidth || y > innerHeight) return false;
      const top = document.elementFromPoint(x, y);
      if (!top || el.contains(top) || top.contains(el)) return false;
      for (let node: Element | null = top; node; node = node.parentElement) {
        const position = getComputedStyle(node).position;
        if (position === "fixed" || position === "sticky") return true;
      }
      return false;
    };
    const inset = 2;
    const points = [
      [box.left + box.width / 2, box.top + box.height / 2],
      [box.left + inset, box.top + inset],
      [box.right - inset, box.top + inset],
      [box.left + inset, box.bottom - inset],
      [box.right - inset, box.bottom - inset],
    ] as const;
    const visible = box.width > 0 && box.height > 0;
    return {
      name: `${el.tagName.toLowerCase()}${el.id ? `#${el.id}` : ""}.${String(el.className).split(" ")[0]} "${(el.getAttribute("aria-label") ?? el.textContent ?? "").trim().slice(0, 30)}"`,
      indicated,
      obscured: visible && points.every(([x, y]) => covering(x, y)),
      inMain: !!el.closest("main"),
      skip: el.classList.contains("skip-link"),
    };
  });
}

function overflowFacts(page: Page) {
  return page.evaluate(() => {
    const width = document.documentElement.clientWidth;
    const scrollable = (el: Element | null) => {
      for (let node = el; node && node !== document.body; node = node.parentElement) {
        const s = getComputedStyle(node);
        if (["auto", "scroll"].includes(s.overflowX) && node.scrollWidth > node.clientWidth) return true;
      }
      return false;
    };
    const wide = [...document.querySelectorAll<HTMLElement>("body *")]
      .filter((el) => {
        const box = el.getBoundingClientRect();
        return (box.right > width + 1 || box.left < -1) && !scrollable(el.parentElement);
      })
      .slice(0, 5)
      .map((el) => `${el.tagName.toLowerCase()}.${String(el.className).split(" ")[0]}`);
    return { by: document.documentElement.scrollWidth - width, wide };
  });
}

/** Text cut off by a container that hides its overflow, when the full text is not in a title or accessible name. */
function clippedText(page: Page) {
  return page.evaluate(() =>
    [...document.querySelectorAll<HTMLElement>("main *, header *, nav *")]
      .filter((el) => {
        if (!el.offsetParent || !el.textContent?.trim() || el.children.length > 0) return false;
        const s = getComputedStyle(el);
        const hides = s.overflow === "hidden" || s.overflowX === "hidden" || s.overflowY === "hidden" || s.overflow === "clip";
        if (!hides) return false;
        // Text hidden on purpose for screen readers only (a one pixel clipped box) is not cut off.
        if (el.getBoundingClientRect().width <= 1 || s.clipPath === "inset(50%)" || el.closest(".visually-hidden")) return false;
        const named = el.title || el.getAttribute("aria-label") || el.closest("[title], [aria-label]");
        const cut = el.scrollWidth > el.clientWidth + 1 || el.scrollHeight > el.clientHeight + 2;
        return cut && !named;
      })
      .slice(0, 5)
      .map((el) => `${el.tagName.toLowerCase()}.${String(el.className).split(" ")[0]} "${el.textContent!.trim().slice(0, 30)}"`),
  );
}

/** Visible controls in the page that are smaller than 24 by 24 pixels, except links inside running text. */
function smallTargets(page: Page) {
  return page.evaluate(() =>
    [...document.querySelectorAll<HTMLElement>("button, [role=tab], [role=menuitem], [role=option], input:not([type=hidden]), select, textarea, a[href]")]
      .filter((el) => el.offsetParent !== null && getComputedStyle(el).visibility !== "hidden")
      .filter((el) => !(el.tagName === "A" && getComputedStyle(el).display === "inline" && el.closest("p, li, td, dd, span")))
      .filter((el) => !el.closest("nextjs-portal"))
      // A cover link stretches its hit area over the whole card with an absolutely placed ::after.
      .filter((el) => {
        const after = getComputedStyle(el, "::after");
        return !(after.content !== "none" && after.position === "absolute");
      })
      .map((el) => ({ el, box: el.getBoundingClientRect() }))
      .filter(({ box }) => box.width > 0 && (box.width < 24 || box.height < 24))
      // The WCAG 2.5.8 spacing exception: a 24 pixel circle around the target's centre overlaps no other target.
      .filter(({ el, box }) => {
        const cx = box.left + box.width / 2;
        const cy = box.top + box.height / 2;
        return [...document.querySelectorAll<HTMLElement>("button, a[href], input, select, [role=tab]")].some((other) => {
          if (other === el || other.contains(el) || el.contains(other) || other.offsetParent === null) return false;
          const o = other.getBoundingClientRect();
          const dx = Math.max(o.left - cx, 0, cx - o.right);
          const dy = Math.max(o.top - cy, 0, cy - o.bottom);
          return Math.hypot(dx, dy) < 12;
        });
      })
      .slice(0, 5)
      .map(({ el, box }) => `${el.outerHTML.slice(0, 70)} ${Math.round(box.width)}x${Math.round(box.height)}`),
  );
}

/** Resizes and waits two frames and a moment, so layouts that respond in script (widget budgets) settle first. */
async function resize(page: Page, width: number, height: number) {
  await page.setViewportSize({ width, height });
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.waitForTimeout(300);
}

const textSpacing = `* { line-height: 1.5 !important; letter-spacing: 0.12em !important; word-spacing: 0.16em !important; }
p { margin-block-end: 2em !important; }`;

for (const design of designs) {
  for (const locale of locales) {
    test.describe(`${design} design in ${locale}`, () => {
      // Default mode: a failure in one check still lets the other run (in a fresh worker with its own tenant).
      test.describe.configure({ mode: "default" });
      test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      let tenant: TestTenant;

      test.beforeAll(async () => {
        tenant = await createTenant(`kbd-${design}-${locale}`, locale, "enterprise_gov");
        await seedHome(tenant);
        await setup(tenant, design, { mode: "light" });
      });

      test("keyboard reaches main by the skip link, and every Tab stop shows focus and stays in view", async ({ page }) => {
        test.setTimeout(20 * 60_000);
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        for (const width of [{ width: 1280, height: 900 }, { width: 390, height: 844 }]) {
          await resize(page, width.width, width.height);
          for (const path of primary) {
            const where = `${path} at ${width.width} px`;
            await page.goto(`${hostOrigin(tenant.key)}${path}`);
            await settle(page);
            expect.soft(await page.locator("[tabindex]:not([tabindex='0']):not([tabindex='-1'])").count(), `positive tabindex on ${where}`).toBe(0);
            await page.keyboard.press("Tab");
            expect.soft((await focusFacts(page))?.skip, `skip link is the first Tab stop on ${where}`).toBe(true);
            await page.keyboard.press("Enter");
            await page.keyboard.press("Tab");
            expect.soft((await focusFacts(page))?.inMain, `the skip link moves focus into main on ${where}`).toBe(true);

            await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
            await page.evaluate(() => window.scrollTo(0, 0));
            const unindicated: string[] = [];
            const obscured: string[] = [];
            for (let i = 0; i < tabStops; i++) {
              await page.keyboard.press("Tab");
              const facts = await focusFacts(page);
              if (!facts) break;
              if (!facts.indicated) unindicated.push(facts.name);
              if (facts.obscured) obscured.push(facts.name);
            }
            expect.soft(unindicated.slice(0, 5), `Tab stops without a visible focus indicator on ${where}`).toEqual([]);
            expect.soft(obscured.slice(0, 5), `Tab stops hidden behind sticky or fixed parts on ${where}`).toEqual([]);
          }
        }
      });

      test("targets, reflow, 200 percent text, text spacing and reduced motion", async ({ page }) => {
        test.setTimeout(20 * 60_000);
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        await page.emulateMedia({ reducedMotion: "reduce" });
        for (const path of primary) {
          await page.setViewportSize({ width: 1280, height: 900 });
          await page.goto(`${hostOrigin(tenant.key)}${path}`);
          await settle(page);
          expect.soft(await smallTargets(page), `targets under 24 by 24 pixels on ${path}`).toEqual([]);
          const moving = await page.evaluate(() =>
            document
              .getAnimations()
              .filter((a) => a.playState === "running")
              .map((a) => Number(a.effect?.getComputedTiming().duration ?? 0))
              .filter((ms) => ms > 200),
          );
          expect.soft(moving, `animations over 200 ms with reduced motion on ${path}`).toEqual([]);

          await resize(page, 320, 720);
          const reflow = await overflowFacts(page);
          expect.soft(reflow.by, `horizontal scrolling at 320 px on ${path}: ${reflow.wide.join(", ")}`).toBeLessThanOrEqual(1);
          expect.soft(await smallTargets(page), `targets under 24 by 24 pixels at 320 px on ${path}`).toEqual([]);

          await resize(page, 1280, 900);
          const style = await page.addStyleTag({ content: textSpacing });
          expect.soft(await clippedText(page), `text cut off with WCAG text spacing on ${path}`).toEqual([]);
          await style.evaluate((node) => (node as HTMLStyleElement).remove());
        }

        // 200 percent text (WCAG 1.4.4) through the person's own text size, which scales the root font.
        await setup(tenant, design, { text_scale: "200" });
        try {
          for (const path of primary) {
            await page.setViewportSize({ width: 1280, height: 900 });
            await page.goto(`${hostOrigin(tenant.key)}${path}`);
            await settle(page);
            const zoom = await overflowFacts(page);
            expect.soft(zoom.by, `horizontal scrolling with 200 percent text on ${path}: ${zoom.wide.join(", ")}`).toBeLessThanOrEqual(1);
            expect.soft(await clippedText(page), `text cut off with 200 percent text on ${path}`).toEqual([]);
          }
        } finally {
          await setup(tenant, design, { text_scale: "100" });
        }
      });
    });
  }
}
