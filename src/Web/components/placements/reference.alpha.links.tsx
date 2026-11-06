"use client";

import type { PlacementProps } from "@/lib/experience/contract";

/** Reference pack alpha: a list of links. Contract test component only. */
export default function LinkList({ props }: PlacementProps) {
  const links = Array.isArray(props.links) ? props.links.filter((l): l is string => typeof l === "string") : [];
  const title = typeof props.title === "string" ? props.title : "";
  return (
    <nav className="ref-card" aria-label={title || undefined}>
      {title ? <h2 className="ref-heading">{title}</h2> : null}
      <ul className="ref-list">
        {links.map((href) => (
          <li key={href}>
            <a href={href}>
              <bdi>{href}</bdi>
            </a>
          </li>
        ))}
      </ul>
    </nav>
  );
}
