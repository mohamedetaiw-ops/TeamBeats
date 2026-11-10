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
  level = 1,
}: {
  title: string;
  description?: ReactNode;
  icon?: IconName;
  actions?: ReactNode;
  nav?: ReactNode;
  eyebrow?: ReactNode;
  /** 1 on a page. A page pattern shown inside another page, such as in the gallery, takes the next level down. */
  level?: 1 | 2 | 3;
}) {
  const Heading = `h${level}` as const;
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
          <Heading className="ui-page-header-title">
            <bdi>{title}</bdi>
          </Heading>
          {description ? <p className="ui-page-header-description">{description}</p> : null}
        </div>
        {actions ? <div className="ui-page-header-actions">{actions}</div> : null}
      </div>
      {nav}
    </header>
  );
}
