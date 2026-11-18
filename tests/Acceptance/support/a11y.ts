import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, type TestInfo } from "@playwright/test";
import { exclusions } from "../a11y/exclusions";

/** The WCAG 2.0, 2.1 and 2.2 level A and AA rules (spec 038 A11Y-006). */
export const wcagTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22a", "wcag22aa"];

/** Fonts and images loaded, so contrast and size are measured on what a person sees. */
export async function settle(page: Page): Promise<void> {
  await page.waitForLoadState("networkidle", { timeout: 15_000 }).catch(() => undefined);
  await page.evaluate(async () => {
    await document.fonts.ready;
    await Promise.all(
      [...document.images]
        .filter((img) => !img.complete)
        .map(
          (img) =>
            new Promise((resolve) => {
              img.addEventListener("load", resolve, { once: true });
              img.addEventListener("error", resolve, { once: true });
              setTimeout(resolve, 3000);
            }),
        ),
    );
  });
}

/**
 * The conformance gate (spec 038 A11Y-006): zero critical and zero serious axe violations, apart from the listed
 * exclusions. Every finding, blocking or not, is attached to the report.
 */
export async function expectConformant(page: Page, testInfo: TestInfo, label: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze();
  if (results.violations.length > 0) {
    await testInfo.attach(`axe-${label}.json`, { body: JSON.stringify(results.violations, null, 2), contentType: "application/json" });
  }
  const blocking = results.violations
    .filter((v) => v.impact === "critical" || v.impact === "serious")
    .map((v) => ({
      ...v,
      // An exclusion removes one rule on the elements its selector names, never the rule everywhere.
      nodes: v.nodes.filter((n) => !exclusions.some((e) => e.rule === v.id && n.target.join(" ").includes(e.selector))),
    }))
    .filter((v) => v.nodes.length > 0);
  expect
    .soft(
      // The first node's own message (for contrast: the colours and the ratio) makes a CI failure readable without the report.
      blocking.map((v) => `${v.impact} ${v.id}: ${v.help} (${v.nodes.slice(0, 5).map((n) => n.target.join(" ")).join(", ")}) ${v.nodes[0]?.any[0]?.message ?? ""}`.trim()),
      `serious or critical axe violations on ${label}`,
    )
    .toEqual([]);
}
