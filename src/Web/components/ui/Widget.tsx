"use client";

import Link from "next/link";
import { createContext, useContext, useId, useMemo, type ReactNode } from "react";
import { useTranslations } from "next-intl";

/** The platform content budgets of spec 036 DLS-101, by the width of the region on the 12 column grid. */
export type WidgetBudgetSize = 3 | 5 | 8;

const BudgetContext = createContext<{ size: WidgetBudgetSize; cap: number | null } | null>(null);

/** Quarter and third width regions show 3 items, half and two thirds 5, wider regions 8. */
export function budgetForSpan(span: number): WidgetBudgetSize {
  return span <= 4 ? 3 : span <= 8 ? 5 : 8;
}

/**
 * Set by the page runtime around each placement, so the frame knows the budget of the region it sits in. `cap` is the
 * component's own lower budget from its manifest (spec 032 FR-071); it can lower the region's budget and never raise it.
 */
export function WidgetBudget({ span, cap, children }: { span: number; cap?: number | null; children: ReactNode }) {
  const size = budgetForSpan(span);
  const value = useMemo(() => ({ size, cap: cap ?? null }), [size, cap]);
  return <BudgetContext.Provider value={value}>{children}</BudgetContext.Provider>;
}

/**
 * How many items a summary may show: the lower of what the component asks for and the budget of its region. A
 * component may lower its budget and never raise it. Outside a budgeted region (an inner page) the request stands.
 */
export function useWidgetBudget(requested?: number): number {
  const region = useContext(BudgetContext);
  if (region === null) {
    return requested ?? 5;
  }
  const budget = region.cap === null ? region.size : Math.max(1, Math.min(Math.floor(region.cap), region.size));
  return requested === undefined ? budget : Math.max(1, Math.min(Math.floor(requested), budget));
}

/**
 * Widget (spec 036 DLS-089, docs/design-system/components.md): the one frame every home widget and every list card of
 * an inner page is built on. A section named by its heading, an optional count, at most one header action, the body,
 * and an optional footer link to the full page. The design decides surface, border, radius and shadow through
 * `.ui-widget`, so no feature draws its own card. `head` replaces the standard header where a part brings its own
 * (the calendar header of DLS-069); the section is then named by `title` through aria-label.
 *
 * Content budget (DLS-101): inside a budgeted region the frame carries `data-budget`, and the style sheet hides list
 * rows past it, so a feature cannot exceed the budget by passing more rows. `more` adds the "View all" footer link
 * with the total when there is more than the widget shows.
 */
export function Widget({
  title,
  count,
  action,
  head,
  footer,
  more,
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
  /** Replaces the standard header; `false` renders no header (a state that fills the frame). */
  head?: ReactNode | false;
  /** A "See N more" or "Open" link under the body. */
  footer?: ReactNode;
  /** The full view and the total number of items; the footer link shows when the total is above `shown`. */
  more?: { href: string; total: number; shown?: number } | null;
  busy?: boolean;
  as?: "section" | "nav" | "article";
  lang?: string;
  testId?: string;
  children?: ReactNode;
}) {
  const id = useId();
  const t = useTranslations("ui.widget");
  const budget = useContext(BudgetContext)?.size ?? null;
  const custom = head !== undefined && head !== null;
  const named = custom ? { "aria-label": title } : { "aria-labelledby": id };
  const viewAll = more && more.total > (more.shown ?? budget ?? 0) ? more : null;
  return (
    <Tag className="ui-widget" {...named} aria-busy={busy || undefined} lang={lang} data-testid={testId} data-budget={budget ?? undefined}>
      {custom ? head : (
        <div className="ui-widget-head">
          <h2 id={id} className="ui-widget-title">
            <bdi>{title}</bdi>
          </h2>
          {count ? <span className="ui-widget-count">{count}</span> : null}
          {action ? <span className="ui-widget-action">{action}</span> : null}
        </div>
      )}
      {children}
      {footer || viewAll ? (
        <div className="ui-widget-foot">
          {footer}
          {viewAll ? (
            <Link href={viewAll.href} data-widget-more="">
              {t("viewAll", { count: viewAll.total })}
            </Link>
          ) : null}
        </div>
      ) : null}
    </Tag>
  );
}
