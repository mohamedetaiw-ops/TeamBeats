/**
 * Wraps a name or other user text in Unicode first strong isolate marks (U+2068 ... U+2069) before it goes into a
 * translated sentence, so an Arabic name inside an English sentence (or the reverse) cannot reorder the words around
 * it (spec 007, docs/design-system/foundations/content.md). Use it for every user-provided value passed to `t()`;
 * in markup, wrap the value in `<bdi>` instead.
 */
export function isolate(value: string): string {
  return `⁨${value}⁩`;
}
