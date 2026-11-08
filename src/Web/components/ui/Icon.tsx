/**
 * The only place direction-specific icons are handled (r0-solution-structure section 6). Icons that point
 * "forward" or "back" mirror in right-to-left layouts; others never do.
 */
const paths = {
  "chevron-forward": "M9 6l6 6-6 6",
  external: "M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5",
  tasks: "M9 11l3 3 8-8M20 12v7a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h9",
  bell: "M6 8a6 6 0 1 1 12 0c0 7 3 9 3 9H3s3-2 3-9M10.3 21a1.94 1.94 0 0 0 3.4 0",
} as const;

export function Icon({ name, mirrorInRtl = false }: { name: keyof typeof paths; mirrorInRtl?: boolean }) {
  return (
    <svg className={mirrorInRtl ? "icon mirror-rtl" : "icon"} viewBox="0 0 24 24" width="16" height="16" aria-hidden="true" focusable="false">
      <path d={paths[name]} fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
