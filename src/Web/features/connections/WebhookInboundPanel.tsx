"use client";

import { useSyncExternalStore } from "react";
import { useFormatter, useTranslations } from "next-intl";
import type { InboundCounts } from "@/lib/integrations/types";

const noChange = () => () => {};

/**
 * The inbound URL of a webhook connection and how many deliveries were accepted or refused, by reason (spec 017 US5
 * scenario 2). The URL is on this tenant's own host, so it is shown with the address the administrator is using.
 */
export function WebhookInboundPanel({ counts }: { counts: InboundCounts }) {
  const t = useTranslations("connections");
  const format = useFormatter();
  const origin = useSyncExternalStore(
    noChange,
    () => window.location.origin,
    () => "",
  );
  return (
    <section className="connections-section" aria-labelledby="inbound-heading">
      <h2 id="inbound-heading">{t("webhooks.inboundTitle")}</h2>
      <p>{t("webhooks.inboundIntro")}</p>
      <dl className="connections-facts">
        <dt>{t("webhooks.inboundUrl")}</dt>
        <dd>
          <bdi dir="ltr">{origin + counts.path}</bdi>
        </dd>
        <dt>{t("webhooks.accepted")}</dt>
        <dd>{format.number(counts.accepted)}</dd>
        <dt>{t("webhooks.refusedSignature")}</dt>
        <dd>{format.number(counts.refusedSignature)}</dd>
        <dt>{t("webhooks.refusedWindow")}</dt>
        <dd>{format.number(counts.refusedWindow)}</dd>
        <dt>{t("webhooks.refusedDuplicate")}</dt>
        <dd>{format.number(counts.refusedDuplicate)}</dd>
        <dt>{t("webhooks.lastReceived")}</dt>
        <dd>{counts.lastReceivedAt ? format.dateTime(new Date(counts.lastReceivedAt), { dateStyle: "medium", timeStyle: "short" }) : t("detail.never")}</dd>
      </dl>
    </section>
  );
}
