"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import Link from "next/link";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { NotificationPanel, SettingsDialog, SignInCard, StatCards, TaskList, type Notice, type Task } from "@/components/ui/Blocks";
import { Button } from "@/components/ui/Button";
import { MonthView, WeekView, type CalendarEvent } from "@/components/ui/CalendarViews";
import { DataGrid } from "@/components/ui/DataGrid";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { FormField, SelectField } from "@/components/ui/FormField";
import { SearchField } from "@/components/ui/Inputs";
import { Pagination } from "@/components/ui/Pagination";
import { SelectionBar } from "@/components/ui/SelectionBar";
import { Switch } from "@/components/ui/Switch";

/** The same fixed day as the inputs gallery, so the baselines do not move. */
const today = "2026-11-10";

type Request = { id: string; title: string; requester: string; department: string; amount: number; status: "approved" | "pending" | "returned"; submitted: string };

const requests: Request[] = [
  { id: "r1", title: "requestLaptop", requester: "Layla Hassan", department: "deptEngineering", amount: 7450, status: "pending", submitted: "2026-11-09" },
  { id: "r2", title: "requestTraining", requester: "Omar Farouk", department: "deptFinance", amount: 3200, status: "approved", submitted: "2026-11-04" },
  { id: "r3", title: "requestTravel", requester: "Sara Nasser", department: "deptSales", amount: 12800, status: "returned", submitted: "2026-11-06" },
  { id: "r4", title: "requestLicences", requester: "Khalid Mansour", department: "deptEngineering", amount: 950, status: "approved", submitted: "2026-11-02" },
  { id: "r5", title: "requestChairs", requester: "Huda Salem", department: "deptPeople", amount: 2100, status: "pending", submitted: "2026-11-08" },
];
const tones: Record<Request["status"], BadgeTone> = { approved: "success", pending: "warning", returned: "danger" };

type Budget = { id: string; line: string; q1: number; q2: number; q3: number };
const quarters = ["q1", "q2", "q3"] as const;

const events: CalendarEvent[] = [
  { id: "e1", title: "eventStandup", day: "2026-11-10", start: "09:00", end: "09:30", tone: "info" },
  { id: "e2", title: "eventReview", day: "2026-11-10", start: "11:00", end: "12:30", tone: "success" },
  { id: "e3", title: "eventInterview", day: "2026-11-10", start: "11:30", end: "12:00", tone: "warning" },
  { id: "e4", title: "eventTownHall", day: "2026-11-11", start: "13:00", end: "14:30" },
  { id: "e5", title: "eventNationalDay", day: "2026-11-12" },
  { id: "e6", title: "eventPayroll", day: "2026-11-12", start: "10:00", end: "11:00", tone: "danger" },
  { id: "e7", title: "eventOneToOne", day: "2026-11-09", start: "15:00", end: "15:30", tone: "info" },
  { id: "e8", title: "eventTraining", day: "2026-11-18", start: "09:00", end: "12:00", tone: "success" },
  { id: "e9", title: "eventStandup", day: "2026-11-17", start: "09:00", end: "09:30", tone: "info" },
  { id: "e10", title: "eventDeadline", day: "2026-11-26" },
];

/** Data blocks of the extended catalogue (spec 036 DLS-038, DLS-039), on realistic workplace data. */
export function DataGallery() {
  const t = useTranslations("components_gallery");
  const locale = useLocale();
  const money = new Intl.NumberFormat(locale, { style: "currency", currency: "AED", maximumFractionDigits: 0 });
  const shortDate = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeZone: "UTC" });
  const [term, setTerm] = useState("");
  const [selected, setSelected] = useState<string[]>([]);
  const [budget, setBudget] = useState<Budget[]>([
    { id: "b1", line: "lineTravel", q1: 42000, q2: 38500, q3: 45000 },
    { id: "b2", line: "lineTraining", q1: 18000, q2: 22000, q3: 19500 },
    { id: "b3", line: "lineEvents", q1: 9500, q2: 31000, q3: 12000 },
  ]);
  const [month, setMonth] = useState(today);
  const [tasks, setTasks] = useState<Task[]>([
    { id: "t1", title: t("schedule.taskApprove"), done: false, due: { text: t("schedule.dueToday"), tone: "warning" }, priority: { text: t("schedule.high"), tone: "danger" }, assignee: "Layla Hassan" },
    { id: "t2", title: t("schedule.taskPolicy"), done: false, due: { text: t("schedule.dueThursday") }, meta: t("schedule.fromPolicies"), assignee: "Omar Farouk" },
    { id: "t3", title: t("schedule.taskOnboarding"), done: true, due: { text: t("schedule.dueYesterday") }, assignee: "Sara Nasser" },
    { id: "t4", title: t("schedule.taskSurvey"), done: false, priority: { text: t("schedule.low") } },
  ]);
  const [notices, setNotices] = useState<Notice[]>([
    { id: "n1", title: t("panels.noticeApproved"), body: t("panels.noticeApprovedBody"), time: t("panels.minutesAgo"), dateTime: "2026-11-10T09:50:00Z", unread: true, icon: "check" },
    { id: "n2", title: t("panels.noticeMention"), body: t("panels.noticeMentionBody"), time: t("panels.hourAgo"), dateTime: "2026-11-10T09:00:00Z", unread: true, actor: "Omar Farouk" },
    { id: "n3", title: t("panels.noticePolicy"), time: t("panels.yesterday"), dateTime: "2026-11-09T12:00:00Z", unread: false, icon: "policy" },
  ]);
  const [settings, setSettings] = useState(false);

  const shown = requests.filter((r) => t(`data.${r.title}`).toLowerCase().includes(term.trim().toLowerCase()));
  const columns: Column<Request>[] = [
    { key: "title", header: t("data.colTitle"), rowHeader: true, cell: (r) => <Link href={`#${r.id}`}>{t(`data.${r.title}`)}</Link>, sortValue: (r) => t(`data.${r.title}`) },
    { key: "requester", header: t("data.colRequester"), cell: (r) => <bdi>{r.requester}</bdi>, sortValue: (r) => r.requester, hideable: true },
    { key: "department", header: t("data.colDepartment"), cell: (r) => t(`data.${r.department}`), hideable: true },
    { key: "amount", header: t("data.colAmount"), align: "end", cell: (r) => money.format(r.amount), sortValue: (r) => r.amount },
    { key: "status", header: t("data.colStatus"), cell: (r) => <Badge tone={tones[r.status]}>{t(`data.${r.status}`)}</Badge> },
    { key: "submitted", header: t("data.colSubmitted"), cell: (r) => shortDate.format(Date.parse(r.submitted)), sortValue: (r) => r.submitted, hideable: true },
  ];

  const total = (b: Budget) => b.q1 + b.q2 + b.q3;
  const budgetColumns: Column<Budget>[] = [
    { key: "line", header: t("data.colLine"), rowHeader: true, cell: (b) => t(`data.${b.line}`) },
    ...quarters.map(
      (q): Column<Budget> => ({
        key: q,
        header: t(`data.${q}`),
        align: "end",
        cell: (b) => (
          <input
            type="text"
            inputMode="numeric"
            aria-label={t("data.cellLabel", { line: t(`data.${b.line}`), quarter: t(`data.${q}`) })}
            value={String(b[q])}
            onChange={(e) => {
              const value = Number(e.currentTarget.value.replace(/\D/g, ""));
              setBudget((all) => all.map((x) => (x.id === b.id ? { ...x, [q]: value } : x)));
            }}
          />
        ),
      }),
    ),
    { key: "total", header: t("data.colTotal"), align: "end", cell: (b) => <output>{money.format(total(b))}</output> },
  ];

  return (
    <>
      <section aria-labelledby="cg-data">
        <h2 id="cg-data">{t("data.title")}</h2>
        <p className="notice">{t("data.intro")}</p>
        <div className="gallery-wide">
          <StatCards
            label={t("data.statsLabel")}
            stats={[
              { key: "open", label: t("data.statOpen"), value: "128", icon: "inbox", change: 0.12, goodWhen: "neither", period: t("data.lastMonth") },
              { key: "time", label: t("data.statTime"), value: t("data.statTimeValue"), icon: "clock", change: -0.15, goodWhen: "down", period: t("data.lastMonth") },
              { key: "satisfaction", label: t("data.statSatisfaction"), value: "92%", icon: "star", change: 0.03, period: t("data.lastQuarter") },
              { key: "overdue", label: t("data.statOverdue"), value: "7", icon: "alert", change: 0.4, goodWhen: "down", period: t("data.lastWeek") },
            ]}
          />
          <DataTable
            caption={t("data.tableCaption")}
            columns={columns}
            rows={shown}
            rowKey={(r) => r.id}
            rowLabel={(r) => t(`data.${r.title}`)}
            selected={selected}
            onSelectedChange={setSelected}
            defaultSort={{ key: "submitted", direction: "descending" }}
            emptyTitle={t("data.noMatch")}
            emptyMessage={t("data.noMatchHint")}
            toolbar={<SearchField label={t("data.search")} value={term} onValueChange={setTerm} />}
            selection={
              selected.length ? (
                <SelectionBar
                  summary={t("data.selected", { count: selected.length })}
                  consequence={t("data.selectedHint")}
                  cancelLabel={t("data.cancel")}
                  onCancel={() => setSelected([])}
                >
                  <Button variant="primary" icon="check">
                    {t("data.approve")}
                  </Button>
                </SelectionBar>
              ) : null
            }
            footer={
              <>
                <span>{t("data.range", { from: 1, to: shown.length, total: 42 })}</span>
                <Pagination page={1} pageCount={9} hrefFor={(p) => `/ui-gallery/components?page=${p}`} />
              </>
            }
          />
          <DataGrid caption={t("data.gridCaption")} columns={budgetColumns} rows={budget} rowKey={(b) => b.id} />
        </div>
      </section>

      <section aria-labelledby="cg-schedule">
        <h2 id="cg-schedule">{t("schedule.title")}</h2>
        <p className="notice">{t("schedule.intro")}</p>
        <div className="gallery-wide">
          <MonthView month={month} today={today} onMonthChange={setMonth} events={events.map((e) => ({ ...e, title: t(`schedule.${e.title}`) }))} />
          <WeekView start="2026-11-08" days={5} today={today} events={events.map((e) => ({ ...e, title: t(`schedule.${e.title}`) }))} />
          <TaskList
            label={t("schedule.tasks")}
            tasks={tasks}
            onToggle={(id, done) => setTasks((all) => all.map((x) => (x.id === id ? { ...x, done } : x)))}
          />
        </div>
      </section>

      <section aria-labelledby="cg-panels">
        <h2 id="cg-panels">{t("panels.title")}</h2>
        <p className="notice">{t("panels.intro")}</p>
        <div className="gallery-columns">
          <NotificationPanel
            title={t("panels.notifications")}
            notices={notices}
            onMarkAllRead={() => setNotices((all) => all.map((n) => ({ ...n, unread: false })))}
            allHref="/ui-gallery/components"
            allLabel={t("panels.seeAll")}
          />
          <div className="gallery-stack">
            <SignInCard
              title={t("panels.signInTitle")}
              intro={t("panels.signInIntro")}
              providers={[{ key: "work", label: t("panels.signInWork"), href: "/ui-gallery/components", icon: "lock" }]}
              dividerLabel={t("panels.or")}
              footer={<Link href="/ui-gallery/components">{t("panels.signInHelp")}</Link>}
            >
              <form className="gallery-stack" onSubmit={(e) => e.preventDefault()}>
                <FormField label={t("panels.email")} name="cg-email" type="email" autoComplete="email" />
                <Button type="submit">{t("panels.sendLink")}</Button>
              </form>
            </SignInCard>
            <div>
              <Button icon="settings" onClick={() => setSettings(true)}>
                {t("panels.openSettings")}
              </Button>
            </div>
          </div>
        </div>
        <SettingsDialog
          open={settings}
          onClose={() => setSettings(false)}
          title={t("panels.settings")}
          description={t("panels.settingsIntro")}
          footer={
            <Button variant="primary" onClick={() => setSettings(false)}>
              {t("panels.done")}
            </Button>
          }
          sections={[
            {
              key: "profile",
              label: t("panels.profile"),
              icon: "user",
              content: (
                <div className="gallery-stack">
                  <FormField label={t("panels.displayName")} name="cg-display" defaultValue="Layla Hassan" />
                  <SelectField
                    label={t("panels.language")}
                    name="cg-language"
                    defaultValue="ar"
                    options={[
                      { value: "ar", label: t("panels.arabic") },
                      { value: "en", label: t("panels.english") },
                    ]}
                  />
                </div>
              ),
            },
            {
              key: "notifications",
              label: t("panels.notifications"),
              icon: "bell",
              content: (
                <div className="gallery-stack">
                  <Switch label={t("panels.emailDigest")} hint={t("panels.emailDigestHint")} defaultChecked />
                  <Switch label={t("panels.mentions")} defaultChecked />
                </div>
              ),
            },
            {
              key: "privacy",
              label: t("panels.privacy"),
              icon: "lock",
              content: <Switch label={t("panels.showPresence")} hint={t("panels.showPresenceHint")} />,
            },
          ]}
        />
      </section>
    </>
  );
}
