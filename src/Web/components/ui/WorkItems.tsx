import Link from "next/link";
import { Badge, type BadgeTone } from "./Badge";
import { Icon } from "./Icon";

export type WorkItem = {
  id: string;
  href: string;
  title: string;
  /** The item's number or key, shown left to right in both directions. */
  number: string;
  /** The rest of the meta line, already joined: the step, who it waits for, who it is for. */
  rest?: string | null;
  status: { text: string; tone?: BadgeTone };
  /** The last change: a machine time, the full date for the tooltip and the relative time shown. */
  changed: { dateTime: string; full: string; text: string };
};

/**
 * Work item rows (spec 036 DLS-061): the title is the row's link and covers the row, then a status badge, one meta
 * line, the last change as a relative time and a chevron because the row opens a page. On a phone the badge and time
 * move to a second line. The feature maps its records to rows; the list holds no rules.
 */
export function WorkItems({ items, testId }: { items: readonly WorkItem[]; testId?: string }) {
  return (
    <ul className="ui-work-items" data-testid={testId}>
      {items.map((item) => (
        <li key={item.id} className="ui-work-item">
          <div className="ui-work-item-main">
            <Link className="ui-work-item-title" href={item.href}>
              <bdi>{item.title}</bdi>
            </Link>
            <span className="ui-work-item-meta">
              <bdi dir="ltr">{item.number}</bdi>
              {item.rest ? <span className="ui-work-item-rest">{item.rest}</span> : null}
            </span>
          </div>
          <Badge tone={item.status.tone}>{item.status.text}</Badge>
          <time className="ui-work-item-when" dateTime={item.changed.dateTime} title={item.changed.full}>
            {item.changed.text}
          </time>
          <Icon name="chevron-forward" size={16} />
        </li>
      ))}
    </ul>
  );
}
