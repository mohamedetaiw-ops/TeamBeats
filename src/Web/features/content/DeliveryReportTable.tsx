"use client";

import { useLocale, useTranslations } from "next-intl";
import { DataTable } from "@/components/ui/DataTable";
import { EmptyCell } from "@/components/ui/EmptyCell";
import type { ChannelReport } from "@/lib/content/types";

const known = ["in_app", "email", "push", "teams", "slack", "sms"] as const;

/** Counts by channel and status of one send (spec 015 US8, FR-022). A status a channel does not track shows the shared empty cell. */
export function DeliveryReportTable({ channels }: { channels: ChannelReport[] }) {
  const t = useTranslations("content.delivery");
  const locale = useLocale();
  const number = new Intl.NumberFormat(locale);
  const count = (value: number | null) => (value === null ? <EmptyCell /> : number.format(value));
  const name = (channel: string) => ((known as readonly string[]).includes(channel) ? t(`channelName.${channel}`) : channel);
  return (
    <DataTable
      caption={t("tableCaption")}
      rows={channels}
      rowKey={(c) => c.channel}
      emptyTitle={t("emptyTitle")}
      emptyMessage={t("emptyMessage")}
      columns={[
        { key: "channel", header: t("channel"), rowHeader: true, cell: (c) => name(c.channel) },
        { key: "delivered", header: t("delivered"), align: "end", cell: (c) => count(c.delivered) },
        { key: "read", header: t("read"), align: "end", cell: (c) => count(c.read) },
        { key: "pending", header: t("pending"), align: "end", cell: (c) => count(c.pending) },
        { key: "failed", header: t("failed"), align: "end", cell: (c) => count(c.failed) },
        { key: "bounced", header: t("bounced"), align: "end", cell: (c) => count(c.bounced) },
        { key: "dropped", header: t("dropped"), align: "end", cell: (c) => count(c.dropped) },
      ]}
    />
  );
}
