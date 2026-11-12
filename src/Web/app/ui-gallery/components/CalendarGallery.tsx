"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { AgendaRow, AvailabilityHeatmap, CalendarHeader, CalendarStats, LayerChips, RangeMonth, WeekStrip } from "@/components/ui/CalendarParts";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { Button } from "@/components/ui/Button";
import { addDays } from "@/components/ui/dates";
import { LAYER_LOOK, LAYERS } from "@/lib/calendar";

/** The same fixed day as the other galleries, so the baselines do not move. */
const today = "2026-11-10";
const week = Array.from({ length: 7 }, (_, i) => addDays("2026-11-09", i));
const markers: Record<string, number[]> = {
  "2026-11-09": [1, 2],
  "2026-11-10": [1, 1, 4, 7],
  "2026-11-11": [1],
  "2026-11-12": [3, 1],
  "2026-11-13": [],
  "2026-11-17": [2],
  "2026-11-24": [1, 4],
};
const shares = [100, 86, 71, 57, 43, 0, 0, 100, 100, 86, 86, 71, 0, 0];

/**
 * The calendar parts of spec 036 DLS-069 to DLS-074 with fixed, fictional data: header and view switch, layer chips,
 * week strip, agenda rows in every state, the month with ranges and team availability.
 */
export function CalendarGallery() {
  const t = useTranslations("components_gallery");
  const [selected, setSelected] = useState(today);
  const [layers, setLayers] = useState<string[]>(["meeting", "company", "holiday", "my_leave", "team_leave"]);
  const [view, setView] = useState("agenda");
  const [open, setOpen] = useState<string | null>("town");
  const options = LAYERS.map((key) => ({ key, label: t(`calendar.layers.${key}`), icon: LAYER_LOOK[key].icon, swatch: LAYER_LOOK[key].swatch }));

  return (
    <section aria-labelledby="cg-calendar">
      <h2 id="cg-calendar">{t("calendar.title")}</h2>
      <p className="notice">{t("calendar.intro")}</p>
      <div className="gallery-columns">
        <div className="cal-card">
          <CalendarHeader
            title={t("calendar.cardTitle")}
            period={t("calendar.period")}
            previousLabel={t("calendar.previous")}
            nextLabel={t("calendar.next")}
            todayLabel={t("calendar.today")}
            onPrevious={() => setSelected(addDays(selected, -7))}
            onNext={() => setSelected(addDays(selected, 7))}
            onToday={() => setSelected(today)}
          >
            <SegmentedControl
              label={t("calendar.view")}
              name="cg-calendar-view"
              value={view}
              onChange={setView}
              options={["agenda", "week", "month"].map((v) => ({ value: v, label: t(`calendar.views.${v}`) }))}
            />
          </CalendarHeader>
          <WeekStrip
            label={t("calendar.week")}
            days={week.map((date) => ({ date, markers: markers[date] ?? [], count: (markers[date] ?? []).length }))}
            selected={selected}
            today={today}
            onSelect={setSelected}
            countLabel={(n) => t("calendar.count", { count: n })}
          />
          <LayerChips label={t("calendar.legend")} layers={options.filter((o) => layers.includes(o.key))} selected={layers} />
          <ul className="ui-cal-agenda">
            <AgendaRow swatch={1} icon="video" layerLabel={t("calendar.layers.meeting")} time={t("calendar.standupTime")} title={t("calendar.standup")} meta={t("calendar.online")} primary={<Button size="sm" variant="primary">{t("calendar.join")}</Button>} />
            <AgendaRow
              swatch={2}
              icon="megaphone"
              layerLabel={t("calendar.layers.company")}
              time={t("calendar.townTime")}
              title={t("calendar.town")}
              meta={t("calendar.hall")}
              open={open === "town"}
              onToggle={() => setOpen(open === "town" ? null : "town")}
              details={<p className="cal-detail-line">{t("calendar.townDetails")}</p>}
            />
            <AgendaRow swatch={6} icon="leave" layerLabel={t("calendar.layers.my_leave")} time={t("calendar.allDay")} title={t("calendar.leave")} status="pending" statusLabel={t("calendar.pending")} />
            <AgendaRow swatch={4} icon="star" layerLabel={t("calendar.layers.important")} time={t("calendar.drillTime")} title={t("calendar.drill")} status="cancelled" statusLabel={t("calendar.cancelled")} />
            <AgendaRow swatch={7} icon="people" layerLabel={t("calendar.layers.team_leave")} time={t("calendar.allDay")} title={t("calendar.teamAway")} />
          </ul>
        </div>
        <div className="cal-card">
          <LayerChips label={t("calendar.legend")} layers={options} selected={layers} onToggle={(k) => setLayers(layers.includes(k) ? layers.filter((l) => l !== k) : [...layers, k])} />
          <RangeMonth
            month={today}
            today={today}
            selected={selected}
            weekStart={1}
            onSelect={setSelected}
            countLabel={(n) => t("calendar.count", { count: n })}
            markers={markers}
            bars={[
              { id: "eid", start: "2026-11-18", end: "2026-11-21", label: t("calendar.holiday"), swatch: 5, kind: "holiday" },
              { id: "leave", start: "2026-11-20", end: "2026-11-26", label: t("calendar.leave"), swatch: 6, kind: "leave", pending: true },
              { id: "fair", start: "2026-11-02", end: "2026-11-04", label: t("calendar.fair"), swatch: 4, kind: "event" },
            ]}
          />
        </div>
      </div>
      <div className="gallery-wide">
        <CalendarStats
          items={[
            { key: "available", value: t("calendar.ofTotal"), label: t("calendar.availableToday") },
            { key: "leave", value: "2", label: t("calendar.onLeave") },
            { key: "soon", value: "3", label: t("calendar.soon") },
          ]}
        />
        <AvailabilityHeatmap
          label={t("calendar.heat")}
          weekStart={1}
          cells={shares.map((share, i) => {
            const date = addDays("2026-11-09", i);
            const working = i % 7 < 5;
            return { date, working, share: working ? share : null, breach: working && share < 70, detail: date };
          })}
          breachLabel={t("calendar.under")}
          nonWorkingLabel={t("calendar.nonWorking")}
          shareLabel={(s) => t("calendar.share", { share: s })}
        />
      </div>
    </section>
  );
}
