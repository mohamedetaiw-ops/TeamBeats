"use client";

import { useTranslations } from "next-intl";
import { StateView } from "@/components/ui/StateView";
import { useWidgetBudget, Widget, WidgetBudget } from "@/components/ui/Widget";

/** A summary that asks for six rows; the budget of its region decides how many it may show. */
function Requests({ rows }: { rows: string[] }) {
  const t = useTranslations("components_gallery.widgetBudget");
  const top = useWidgetBudget(6);
  return (
    <Widget title={t("requests")} more={{ href: "/tasks", total: rows.length, shown: top }}>
      <ul className="ui-widget-list">
        {rows.slice(0, top).map((row) => (
          <li key={row} className="ui-widget-row">
            <span className="ui-widget-row-main">
              <bdi>{row}</bdi>
            </span>
          </li>
        ))}
      </ul>
    </Widget>
  );
}

/**
 * Content budgets on the widget frame (spec 036 DLS-101, DLS-103): the same summary in a third width and a half width
 * region shows 3 and 5 rows and ends in View all with the total, and a placement state fills the same frame.
 */
export function WidgetBudgetGallery() {
  const t = useTranslations("components_gallery.widgetBudget");
  const rows = [t("rowLeave"), t("rowTravel"), t("rowExpense"), t("rowLaptop"), t("rowLetter"), t("rowAccess"), t("rowTraining")];
  return (
    <section aria-labelledby="cg-widget-budget">
      <h2 id="cg-widget-budget">{t("title")}</h2>
      <p className="notice">{t("intro")}</p>
      <div className="gallery-cards">
        <div data-testid="cg-budget-third">
          <WidgetBudget span={4}>
            <Requests rows={rows} />
          </WidgetBudget>
        </div>
        <div data-testid="cg-budget-half">
          <WidgetBudget span={6}>
            <Requests rows={rows} />
          </WidgetBudget>
        </div>
        <div data-testid="cg-budget-state">
          <WidgetBudget span={4}>
            <Widget title={t("failed")} head={false}>
              <StateView kind="error" title={t("failed")} message="" />
            </Widget>
          </WidgetBudget>
        </div>
      </div>
    </section>
  );
}
