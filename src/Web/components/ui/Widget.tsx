import { useId, type ReactNode } from "react";

/**
 * Widget (spec 036 DLS-089, docs/design-system/components.md): the one frame every home widget and every list card of
 * an inner page is built on. A section named by its heading, an optional count, at most one header action, the body,
 * and an optional footer link to the full page. The design decides surface, border, radius and shadow through
 * `.ui-widget`, so no feature draws its own card. `head` replaces the standard header where a part brings its own
 * (the calendar header of DLS-069); the section is then named by `title` through aria-label.
 */
export function Widget({
  title,
  count,
  action,
  head,
  footer,
  busy = false,
  as: Tag = "section",
  lang,
  testId,
  children,
}: {
  title: string;
  /** A count beside the title, usually a Badge. */
  count?: ReactNode;
  /** One header action: a link to the full page, or a button. */
  action?: ReactNode;
  head?: ReactNode;
  /** A "See N more" or "Open" link under the body. */
  footer?: ReactNode;
  busy?: boolean;
  as?: "section" | "nav" | "article";
  lang?: string;
  testId?: string;
  children?: ReactNode;
}) {
  const id = useId();
  const named = head ? { "aria-label": title } : { "aria-labelledby": id };
  return (
    <Tag className="ui-widget" {...named} aria-busy={busy || undefined} lang={lang} data-testid={testId}>
      {head ?? (
        <div className="ui-widget-head">
          <h2 id={id} className="ui-widget-title">
            <bdi>{title}</bdi>
          </h2>
          {count ? <span className="ui-widget-count">{count}</span> : null}
          {action ? <span className="ui-widget-action">{action}</span> : null}
        </div>
      )}
      {children}
      {footer ? <div className="ui-widget-foot">{footer}</div> : null}
    </Tag>
  );
}
