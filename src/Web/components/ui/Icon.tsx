/**
 * The only place direction-specific icons are handled (r0-solution-structure section 6). Icons that point
 * "forward" or "back" mirror in right-to-left layouts; others never do.
 */
const paths = {
  "chevron-forward": "M9 6l6 6-6 6",
  bell: "M6 8a6 6 0 1 1 12 0c0 7 3 9 3 9H3s3-2 3-9M10.3 21a1.94 1.94 0 0 0 3.4 0",
} as const;

export function Icon({ name, mirrorInRtl = false }: { name: keyof typeof paths; mirrorInRtl?: boolean }) {
  return (
    <svg className={mirrorInRtl ? "icon mirror-rtl" : "icon"} viewBox="0 0 24 24" width="16" height="16" aria-hidden="true" focusable="false">
      <path d={paths[name]} fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
