import type { ReactNode } from "react";

/**
 * A fused row of related buttons, such as Day, Week, Month or Previous and Next (spec 036 DLS-033). The group has a
 * name so assistive technology announces what the buttons belong to. For one choice out of a few, where the choice
 * is a setting and not an action, use SegmentedControl.
 */
export function ButtonGroup({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div role="group" aria-label={label} className="ui-button-group">
      {children}
    </div>
  );
}
