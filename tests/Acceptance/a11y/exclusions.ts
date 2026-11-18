/**
 * Accessibility gate exclusions (spec 038 A11Y-008). Each entry names one axe rule on one selector, why it is not a
 * real failure or why it waits, and the date it must be looked at again. An entry without a reason or past its review
 * date fails the suite, and a rule is never turned off for the whole page.
 */
export type Exclusion = { rule: string; selector: string; reason: string; review: string };

export const exclusions: readonly Exclusion[] = [];
