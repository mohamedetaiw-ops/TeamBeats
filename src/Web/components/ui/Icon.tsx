/**
 * The only place direction-specific icons are handled (r0-solution-structure section 6). Icons that point
 * "forward" or "back" mirror in right-to-left layouts; others never do.
 */
const paths = {
  "chevron-forward": "M9 6l6 6-6 6",
} as const;

export function Icon({ name, mirrorInRtl = false }: { name: keyof typeof paths; mirrorInRtl?: boolean }) {
  return (
    <svg className={mirrorInRtl ? "icon mirror-rtl" : "icon"} viewBox="0 0 24 24" width="16" height="16" aria-hidden="true" focusable="false">
      <path d={paths[name]} fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
