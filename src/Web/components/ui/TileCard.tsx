import Link from "next/link";
import type { ReactNode } from "react";
import { Icon, type IconName } from "./Icon";

export type TileTone = "accent" | "highlight" | "success" | "info";

/**
 * Tile card (docs/design-system/components.md, Tile card): an icon tile or avatar, a title that is the one link of the
 * card and covers it, a short meta line, and an optional footer action that stays its own target above the cover.
 * Used for libraries, knowledge articles, services and people, so every catalogue looks and reads the same.
 */
export function TileCard({
  title,
  href,
  icon,
  tone = "accent",
  leading,
  meta,
  children,
  footer,
  external,
  headingLevel = 3,
  testId,
}: {
  title: string;
  href: string;
  icon?: IconName;
  tone?: TileTone;
  leading?: ReactNode;
  meta?: ReactNode;
  children?: ReactNode;
  footer?: ReactNode;
  external?: { label: string };
  headingLevel?: 2 | 3 | 4;
  testId?: string;
}) {
  const Heading = `h${headingLevel}` as const;
  return (
    <article className="ui-tile" data-testid={testId}>
      <div className="ui-tile-head">
        {leading ??
          (icon ? (
            <span className="ui-icon-tile" data-tone={tone}>
              <Icon name={icon} size={22} />
            </span>
          ) : null)}
        <div className="ui-tile-text">
          <Heading className="ui-tile-title">
            {external ? (
              <a className="ui-tile-link" href={href} target="_blank" rel="noopener noreferrer">
                <bdi>{title}</bdi>
                <Icon name="external" label={external.label} size={16} />
              </a>
            ) : (
              <Link className="ui-tile-link" href={href}>
                <bdi>{title}</bdi>
              </Link>
            )}
          </Heading>
          {meta ? <p className="ui-tile-meta">{meta}</p> : null}
        </div>
      </div>
      {children}
      {footer ? <div className="ui-tile-footer">{footer}</div> : null}
    </article>
  );
}
