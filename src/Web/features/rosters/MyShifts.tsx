"use client";

import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { DataTable } from "@/components/ui/DataTable";
import { patternName, type MyShift } from "@/lib/rosters";

/** My published shifts for the next two weeks (spec 022 US10), shown on the attendance page. */
export function MyShifts({ shifts }: { shifts: MyShift[] }) {
  const t = useTranslations("rosters.mine");
  const locale = useLocale();
  const format = useFormatter();
  return (
    <section aria-labelledby="my-shifts">
      <h2 id="my-shifts">{t("title")}</h2>
      <DataTable<MyShift>
        caption={t("caption")}
        rows={shifts}
        rowKey={(r) => r.date}
        density="compact"
        emptyTitle={t("empty")}
        columns={[
          {
            key: "date",
            header: t("date"),
            rowHeader: true,
            cell: (r) => format.dateTime(new Date(`${r.date}T00:00:00`), { weekday: "long", day: "numeric", month: "long" }),
          },
          {
            key: "shift",
            header: t("shift"),
            cell: (r) => (r.pattern ? <Badge tone={r.pattern.tone}>{patternName(r.pattern, locale)}</Badge> : r.dayOff ? t("dayOff") : t("none")),
          },
          { key: "hours", header: t("hours"), cell: (r) => (r.pattern ? <bdi>{t("hoursRange", { start: r.pattern.start, end: r.pattern.end })}</bdi> : t("none")) },
        ]}
      />
    </section>
  );
}
