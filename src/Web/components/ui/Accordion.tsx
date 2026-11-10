import type { ReactNode } from "react";
import { Icon } from "./Icon";

export type AccordionItem = { key: string; title: string; content: ReactNode; open?: boolean };

/**
 * Accordion (spec 036 DLS-038): questions and settings that open in place. Each item is a native details element, so
 * it works without script, is found by the browser's find in page, and Enter or Space toggles it. With `exclusive`,
 * opening one item closes the others.
 */
export function Accordion({ items, exclusive, headingLevel = 3 }: { items: readonly AccordionItem[]; exclusive?: string; headingLevel?: 2 | 3 | 4 }) {
  const Heading = `h${headingLevel}` as "h2" | "h3" | "h4";
  return (
    <div className="ui-accordion">
      {items.map((item) => (
        <details key={item.key} name={exclusive} open={item.open}>
          <summary>
            <Heading>{item.title}</Heading>
            <Icon name="chevron-down" size={18} />
          </summary>
          <div className="ui-accordion-body">{item.content}</div>
        </details>
      ))}
    </div>
  );
}
