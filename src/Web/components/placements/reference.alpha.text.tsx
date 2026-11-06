"use client";

import { localized, type PlacementProps } from "@/lib/experience/contract";

/** Reference pack alpha: heading and plain text. Contract test component only (spec 032 swap test S02). */
export default function TextBlock({ props, context }: PlacementProps) {
  const heading = localized(props.heading, context.locale);
  return (
    <article className="ref-card">
      {heading ? <h2 className="ref-heading">{heading}</h2> : null}
      <p>{localized(props.body, context.locale)}</p>
    </article>
  );
}
