/**
 * Accessibility gate exclusions (spec 038 A11Y-008). Each entry names one axe rule on one selector, why it is not a
 * real failure or why it waits, and the date it must be looked at again. An entry without a reason or past its review
 * date fails the suite, and a rule is never turned off for the whole page.
 */
export type Exclusion = { rule: string; selector: string; reason: string; review: string };

export const exclusions: readonly Exclusion[] = [];

/**
 * Detail routes with no record that an end-to-end suite can build yet (spec 038 T022). Each names why and the date it
 * must be looked at again; the inventory fails for any other suite-checked detail route without its matrix check.
 */
export type Unseeded = { route: string; reason: string; review: string };

export const unseededRoutes: readonly Unseeded[] = [
  {
    route: "/admin/agents/security/alerts/[id]",
    reason: "an alert is raised by the worker's anomaly pass after ten refused tool calls of a published agent; the integration suite covers the pass, and the page shares its parts with the checked incident page",
    review: "2027-02-28",
  },
  {
    route: "/admin/evaluation/review/[id]",
    reason: "a review item comes from a production sample of a real agent run that the sampling worker keeps; the checked review queue and case pages share its parts",
    review: "2027-02-28",
  },
  {
    route: "/agents/approvals/[id]",
    reason: "an approval needs an L3 agent whose tool rule is approval, which only the integration suite's test tools reach; the checked AI action page shares its parts",
    review: "2027-02-28",
  },
];
