"use client";

import { useLocale } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import "@/features/content/content.css";

const safe = (href: string) => /^(https?:\/\/|mailto:|\/(?!\/))/i.test(href);

/**
 * The call to action component (spec 009 FR-030): an optional eyebrow, a heading, a short text and one link styled as a
 * button. "campaign" is the full width band that closes the home (spec 008 home composition), in the campaign colour.
 */
export default function CallToAction({ props }: PlacementProps) {
  const locale = useLocale();
  const heading = localized(props.heading, locale);
  const body = localized(props.body, locale);
  const label = localized(props.label, locale);
  const link = typeof props.link === "string" && safe(props.link) ? props.link : null;
  const eyebrow = localized(props.eyebrow, locale);
  const emphasis = props.emphasis === "secondary" || props.emphasis === "campaign" ? props.emphasis : "primary";
  return (
    <section className="content-card content-cta" data-emphasis={emphasis} aria-label={heading || undefined}>
      {eyebrow ? (
        <p className="content-cta-eyebrow">
          <bdi>{eyebrow}</bdi>
        </p>
      ) : null}
      {heading ? (
        <h2>
          <bdi>{heading}</bdi>
        </h2>
      ) : null}
      {body ? (
        <p className="content-meta">
          <bdi>{body}</bdi>
        </p>
      ) : null}
      {link && label ? (
        <a className="content-cta-link" data-emphasis={emphasis} href={link}>
          <bdi>{label}</bdi>
        </a>
      ) : null}
    </section>
  );
}
