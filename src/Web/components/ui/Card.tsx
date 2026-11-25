import type { ReactNode } from "react";

/**
 * Card (docs/design-system/components.md): one topic per card, a heading that names it, optional media, body and one
 * footer action row. When `href` is set the heading is the link and the whole card is clickable through it, so a screen
 * reader hears one link with the heading as its name. The design decides elevation, border and corner radius.
 */
export function Card({
  title,
  headingLevel = 3,
  href,
  eyebrow,
  media,
  footer,
  children,
  tone = "default",
}: {
  title: string;
  headingLevel?: 2 | 3 | 4;
  href?: string;
  eyebrow?: ReactNode;
  media?: ReactNode;
  footer?: ReactNode;
  children?: ReactNode;
  tone?: "default" | "accent" | "sunken";
}) {
  const Heading = `h${headingLevel}` as const;
  return (
    <article className="ui-card" data-tone={tone} data-linked={href ? true : undefined}>
      {media ? <div className="ui-card-media">{media}</div> : null}
      <div className="ui-card-body">
        {eyebrow ? <p className="ui-card-eyebrow">{eyebrow}</p> : null}
        <Heading className="ui-card-title">
          {href ? (
            <a className="ui-card-link" href={href}>
              <bdi>{title}</bdi>
            </a>
          ) : (
            <bdi>{title}</bdi>
          )}
        </Heading>
        {children}
      </div>
      {footer ? <div className="ui-card-footer">{footer}</div> : null}
    </article>
  );
}

/**
 * Reading article (docs/design-system/components.md, Reading article; spec 036 T037a): the one surface a long read sits
 * on, a news item, a page or a knowledge article. Wide padding, the page title as its h1 and the body in reading size;
 * the feature puts its own header, body and tags inside.
 */
export function ReadingArticle({ lang, children }: { lang?: string; children: ReactNode }) {
  return (
    <article className="ui-reading" lang={lang}>
      {children}
    </article>
  );
}
