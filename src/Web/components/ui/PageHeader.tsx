import type { ReactNode } from "react";
import { Icon, type IconName } from "./Icon";

/**
 * Page header (docs/design-system/components.md): the one h1 of a module page with its icon tile, a one line
 * description, the page's main action on the end side, and the section tabs under it. Every module page starts with it
 * so people always find the title, the main action and the sections in the same place.
 */
export function PageHeader({
  title,
  description,
  icon,
  actions,
  nav,
  eyebrow,
}: {
  title: string;
  description?: ReactNode;
  icon?: IconName;
  actions?: ReactNode;
  nav?: ReactNode;
  eyebrow?: ReactNode;
}) {
  return (
    <header className="ui-page-header">
      <div className="ui-page-header-main">
        {icon ? (
          <span className="ui-icon-tile" data-size="lg">
            <Icon name={icon} size={26} />
          </span>
        ) : null}
        <div className="ui-page-header-text">
          {eyebrow ? <p className="ui-page-header-eyebrow">{eyebrow}</p> : null}
          <h1 className="ui-page-header-title">
            <bdi>{title}</bdi>
          </h1>
          {description ? <p className="ui-page-header-description">{description}</p> : null}
        </div>
        {actions ? <div className="ui-page-header-actions">{actions}</div> : null}
      </div>
      {nav}
    </header>
  );
}
