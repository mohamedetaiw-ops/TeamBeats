"use client";

import { useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import "@/features/content/content.css";

/** Only links a reader can safely open; the manifest already validates url properties, this keeps the client strict too. */
const safe = (href: string) => /^(https?:\/\/|mailto:|\/(?!\/))/i.test(href);

/** The quick links component (spec 009 FR-030): a titled list of links, each with an optional localized label. */
export default function QuickLinks({ props }: PlacementProps) {
  const t = useTranslations("content.component.links");
  const locale = useLocale();
  const title = localized(props.title, locale) || t("title");
  const hrefs = Array.isArray(props.links) ? props.links : [];
  const labels = Array.isArray(props.labels) ? props.labels : [];
  const entries = hrefs
    .map((href, i) => ({ href: typeof href === "string" ? href : "", label: localized(labels[i], locale) }))
    .filter((e) => e.href && safe(e.href));
  return (
    <Widget as="nav" title={title}>
      {entries.length === 0 ? (
        <StateView kind="empty" title={t("none")} message="" />
      ) : (
        <ul className="content-list">
          {entries.map((e, i) => (
            <li key={`${i}-${e.href}`}>
              <a href={e.href}>
                <bdi>{e.label || e.href}</bdi>
              </a>
            </li>
          ))}
        </ul>
      )}
    </Widget>
  );
}
