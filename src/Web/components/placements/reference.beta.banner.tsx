"use client";

import { localized, type PlacementProps } from "@/lib/experience/contract";

/** Reference pack beta: an image banner. Asset delivery comes later, so the image is a sized, labelled area. */
export default function ImageBanner({ props, context }: PlacementProps) {
  const alt = localized(props.alt, context.locale);
  const link = typeof props.link === "string" ? props.link : null;
  const image = <div className="ref-banner" role="img" aria-label={alt} />;
  return link ? <a href={link}>{image}</a> : image;
}
