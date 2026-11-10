"use client";

import { useState, useSyncExternalStore } from "react";
import { useLocale, useTranslations } from "next-intl";
import {
  Cite,
  Composer,
  SourceList,
  ThinkingIndicator,
  UsageLimit,
} from "@/components/ui/Assistant";
import { Avatar } from "@/components/ui/Avatar";
import { Badge } from "@/components/ui/Badge";
import { StatCards, TaskList, type Task } from "@/components/ui/Blocks";
import { Button } from "@/components/ui/Button";
import {
  BarList,
  ChartCard,
  LineChart,
  StageBars,
  seriesTable,
  type Series,
} from "@/components/ui/Charts";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { Menu } from "@/components/ui/Menu";
import { PageHeader } from "@/components/ui/PageHeader";
import {
  Board,
  ChatLayout,
  ChatTranscript,
  FactList,
  Mosaic,
  ProfileBlock,
  ProfileIcon,
  ProfileLayout,
  Timeline,
  type BoardCard,
  type ChatMessage,
} from "@/components/ui/Patterns";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { Tabs } from "@/components/ui/Tabs";

const noSubscription = () => () => {};
const months = [
  "2026-06-01",
  "2026-07-01",
  "2026-08-01",
  "2026-09-01",
  "2026-10-01",
  "2026-11-01",
];

type Run = {
  id: string;
  started: string;
  trigger: string;
  status: "done" | "failed" | "running";
  duration: string;
};

/** The page patterns, with the content of real pages, so the design suite checks them in every design and direction. */
export function PatternsGallery() {
  const t = useTranslations("patterns_gallery");
  const locale = useLocale();
  const hydrated = useSyncExternalStore(
    noSubscription,
    () => true,
    () => false,
  );
  const number = new Intl.NumberFormat(locale, { maximumFractionDigits: 0 });
  const compact = new Intl.NumberFormat(locale, {
    notation: "compact",
    maximumFractionDigits: 1,
  });
  const monthName = new Intl.DateTimeFormat(locale, {
    month: "short",
    timeZone: "UTC",
  });
  const categories = months.map((m) => monthName.format(Date.parse(m)));
  const n = (v: number) => number.format(v);
  const c = (v: number) => compact.format(v);

  // Dashboard.
  const [period, setPeriod] = useState("month");
  const [tasks, setTasks] = useState<Task[]>([
    {
      id: "t1",
      title: t("dashboard.task1"),
      done: false,
      due: { text: t("dashboard.today"), tone: "warning" },
      assignee: t("people.layla"),
    },
    {
      id: "t2",
      title: t("dashboard.task2"),
      done: false,
      due: { text: t("dashboard.tomorrow") },
      assignee: t("people.omar"),
    },
    {
      id: "t3",
      title: t("dashboard.task3"),
      done: true,
      assignee: t("people.sara"),
    },
  ]);
  const visits: Series[] = [
    {
      key: "web",
      label: t("dashboard.web"),
      values: [4200, 4800, 4500, 5600, 6100, 6900],
    },
    {
      key: "mobile",
      label: t("dashboard.mobile"),
      values: [1800, 2300, 2900, 3400, 4200, 5100],
    },
  ];

  // Chat.
  const [conversation, setConversation] = useState("c1");
  const [draft, setDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<ChatMessage[]>([
    {
      id: "m1",
      from: "person",
      author: t("chat.you"),
      body: <p>{t("chat.question")}</p>,
    },
    {
      id: "m2",
      from: "assistant",
      author: t("chat.assistant"),
      time: t("chat.time"),
      body: (
        <p>
          {t("chat.answerStart")}
          <Cite number={1} idPrefix="pt-src" title={t("chat.sourceBalance")} />
          {t("chat.answerMiddle")}
          <Cite number={2} idPrefix="pt-src" title={t("chat.sourcePolicy")} />
          {t("chat.answerEnd")}
        </p>
      ),
      extra: (
        <SourceList
          label={t("chat.sources")}
          idPrefix="pt-src"
          sources={[
            {
              key: "balance",
              title: t("chat.sourceBalance"),
              href: "#pt-src-1",
              origin: t("chat.originHr"),
              icon: "user",
            },
            {
              key: "policy",
              title: t("chat.sourcePolicy"),
              href: "#pt-src-2",
              origin: t("chat.originPolicies"),
              icon: "policy",
            },
          ]}
        />
      ),
    },
  ]);
  function send(text: string) {
    setMessages((list) => [
      ...list,
      {
        id: `m${list.length + 1}`,
        from: "person",
        author: t("chat.you"),
        body: <p>{text}</p>,
      },
    ]);
    setDraft("");
    setBusy(true);
  }

  // Agent runs.
  const runs: Run[] = [
    {
      id: "r1",
      started: t("agent.run1"),
      trigger: t("agent.triggerSchedule"),
      status: "running",
      duration: t("agent.duration1"),
    },
    {
      id: "r2",
      started: t("agent.run2"),
      trigger: t("agent.triggerPerson"),
      status: "done",
      duration: t("agent.duration2"),
    },
    {
      id: "r3",
      started: t("agent.run3"),
      trigger: t("agent.triggerSchedule"),
      status: "failed",
      duration: t("agent.duration3"),
    },
  ];
  const runTone = {
    done: "success",
    failed: "danger",
    running: "info",
  } as const;
  const runColumns: Column<Run>[] = [
    {
      key: "started",
      header: t("agent.started"),
      cell: (r) => r.started,
      rowHeader: true,
    },
    { key: "trigger", header: t("agent.trigger"), cell: (r) => r.trigger },
    {
      key: "status",
      header: t("agent.status"),
      cell: (r) => (
        <Badge tone={runTone[r.status]}>{t(`agent.${r.status}`)}</Badge>
      ),
    },
    {
      key: "duration",
      header: t("agent.duration"),
      cell: (r) => r.duration,
      align: "end",
    },
  ];

  // Board.
  const columns = [
    { key: "new", label: t("board.new") },
    { key: "progress", label: t("board.progress") },
    { key: "waiting", label: t("board.waiting") },
    { key: "done", label: t("board.done") },
  ];
  const [cards, setCards] = useState<BoardCard[]>([
    {
      id: "b1",
      title: t("board.card1"),
      column: "new",
      meta: t("board.meta1"),
      tags: [t("board.tagIt")],
      assignee: t("people.omar"),
    },
    {
      id: "b2",
      title: t("board.card2"),
      column: "new",
      meta: t("board.meta2"),
      tags: [t("board.tagHr")],
      due: { text: t("dashboard.tomorrow") },
    },
    {
      id: "b3",
      title: t("board.card3"),
      column: "progress",
      meta: t("board.meta3"),
      tags: [t("board.tagIt")],
      due: { text: t("dashboard.today"), tone: "warning" },
      assignee: t("people.layla"),
    },
    {
      id: "b4",
      title: t("board.card4"),
      column: "progress",
      meta: t("board.meta4"),
      tags: [t("board.tagFacilities")],
      assignee: t("people.khalid"),
    },
    {
      id: "b5",
      title: t("board.card5"),
      column: "waiting",
      meta: t("board.meta5"),
      tags: [t("board.tagFinance")],
      assignee: t("people.huda"),
    },
    {
      id: "b6",
      title: t("board.card6"),
      column: "done",
      meta: t("board.meta6"),
      tags: [t("board.tagHr")],
      assignee: t("people.sara"),
    },
  ]);

  return (
    <div
      className="gallery"
      data-testid="patterns-gallery"
      data-hydrated={hydrated}
    >
      <section aria-labelledby="pt-dashboard">
        <h2 id="pt-dashboard">{t("dashboard.title")}</h2>
        <p className="notice">{t("dashboard.intro")}</p>
        <div className="gallery-pattern">
          <PageHeader
            level={3}
            icon="trend-up"
            title={t("dashboard.page")}
            description={t("dashboard.description")}
            actions={
              <SegmentedControl
                label={t("dashboard.period")}
                name="pt-period"
                value={period}
                onChange={setPeriod}
                options={[
                  { value: "week", label: t("dashboard.week") },
                  { value: "month", label: t("dashboard.month") },
                  { value: "quarter", label: t("dashboard.quarter") },
                ]}
              />
            }
          />
          <StatCards
            label={t("dashboard.numbers")}
            stats={[
              {
                key: "people",
                label: t("dashboard.activePeople"),
                value: n(1284),
                icon: "people",
                change: 0.12,
                period: t("dashboard.lastMonth"),
              },
              {
                key: "requests",
                label: t("dashboard.openRequests"),
                value: n(245),
                icon: "inbox",
                change: -0.08,
                goodWhen: "down",
                period: t("dashboard.lastMonth"),
              },
              {
                key: "answers",
                label: t("dashboard.answers"),
                value: n(3410),
                icon: "spark",
                change: 0.31,
                period: t("dashboard.lastMonth"),
              },
            ]}
          />
          <Mosaic
            tiles={[
              {
                key: "visits",
                span: "two-thirds",
                content: (
                  <ChartCard
                    title={t("dashboard.visits")}
                    description={t("dashboard.visitsHint")}
                    legend={visits}
                    table={seriesTable(
                      t("dashboard.monthColumn"),
                      categories,
                      visits,
                      n,
                    )}
                  >
                    <LineChart
                      label={t("dashboard.visitsSummary")}
                      categories={categories}
                      series={visits}
                      format={c}
                      area
                      stacked
                    />
                  </ChartCard>
                ),
              },
              {
                key: "searches",
                content: (
                  <ChartCard
                    title={t("dashboard.searches")}
                    table={{
                      head: [t("dashboard.term"), t("dashboard.count")],
                      rows: [
                        [t("dashboard.termLeave"), n(1840)],
                        [t("dashboard.termPayslip"), n(1320)],
                        [t("dashboard.termVpn"), n(540)],
                      ],
                    }}
                  >
                    <BarList
                      label={t("dashboard.searches")}
                      items={[
                        {
                          key: "leave",
                          label: t("dashboard.termLeave"),
                          value: 1840,
                          valueText: n(1840),
                        },
                        {
                          key: "payslip",
                          label: t("dashboard.termPayslip"),
                          value: 1320,
                          valueText: n(1320),
                        },
                        {
                          key: "vpn",
                          label: t("dashboard.termVpn"),
                          value: 540,
                          valueText: n(540),
                        },
                      ]}
                    />
                  </ChartCard>
                ),
              },
              {
                key: "tasks",
                span: "half",
                content: (
                  <ProfileBlock title={t("dashboard.attention")} level={4}>
                    <TaskList
                      label={t("dashboard.attention")}
                      tasks={tasks}
                      onToggle={(id, done) =>
                        setTasks((list) =>
                          list.map((x) => (x.id === id ? { ...x, done } : x)),
                        )
                      }
                    />
                  </ProfileBlock>
                ),
              },
              {
                key: "stages",
                span: "half",
                content: (
                  <ChartCard
                    title={t("dashboard.stages")}
                    table={{
                      head: [t("dashboard.stage"), t("dashboard.count")],
                      rows: columns.map((col, i) => [
                        col.label,
                        n([42, 65, 18, 120][i] ?? 0),
                      ]),
                    }}
                  >
                    <StageBars
                      label={t("dashboard.stagesSummary")}
                      stages={columns.map((col, i) => ({
                        key: col.key,
                        label: col.label,
                        value: [42, 65, 18, 120][i] ?? 0,
                      }))}
                      format={n}
                    />
                  </ChartCard>
                ),
              },
            ]}
          />
        </div>
      </section>

      <section aria-labelledby="pt-chat">
        <h2 id="pt-chat">{t("chat.title")}</h2>
        <p className="notice">{t("chat.intro")}</p>
        <ChatLayout
          label={t("chat.conversations")}
          level={3}
          current={conversation}
          onSelect={setConversation}
          onNew={() => setConversation("")}
          groups={[
            {
              key: "today",
              label: t("chat.today"),
              items: [
                { id: "c1", title: t("chat.c1"), time: t("chat.time") },
                { id: "c2", title: t("chat.c2"), time: t("chat.time2") },
              ],
            },
            {
              key: "week",
              label: t("chat.thisWeek"),
              items: [
                { id: "c3", title: t("chat.c3") },
                { id: "c4", title: t("chat.c4") },
                { id: "c5", title: t("chat.c5") },
              ],
            },
          ]}
        >
          <ChatTranscript
            label={t("chat.transcript")}
            messages={messages}
            pending={
              busy ? <ThinkingIndicator step={t("chat.searching")} /> : null
            }
          />
          <Composer
            label={t("chat.composer")}
            placeholder={t("chat.placeholder")}
            value={draft}
            onValueChange={setDraft}
            onSend={send}
            busy={busy}
            onStop={() => setBusy(false)}
            notice={t("chat.notice")}
          />
        </ChatLayout>
      </section>

      <section aria-labelledby="pt-agent">
        <h2 id="pt-agent">{t("agent.title")}</h2>
        <p className="notice">{t("agent.intro")}</p>
        <ProfileLayout
          level={3}
          media={<ProfileIcon icon="spark" />}
          eyebrow={t("agent.eyebrow")}
          title={t("agent.name")}
          status={{
            text: t("agent.published"),
            tone: "success",
            icon: "check",
          }}
          description={t("agent.description")}
          facts={[
            { key: "owner", label: t("agent.owner"), value: t("people.layla") },
            {
              key: "version",
              label: t("agent.version"),
              value: t("agent.versionValue"),
            },
            {
              key: "updated",
              label: t("agent.updated"),
              value: t("agent.updatedValue"),
            },
          ]}
          actions={
            <>
              <Button variant="primary" icon="arrow-forward">
                {t("agent.run")}
              </Button>
              <Menu
                label={t("agent.more")}
                icon="more"
                iconOnly
                placement="bottom-end"
                items={[
                  { key: "edit", label: t("agent.edit"), icon: "edit" },
                  { key: "copy", label: t("agent.duplicate"), icon: "plus" },
                  { kind: "separator", key: "s" },
                  {
                    key: "retire",
                    label: t("agent.retire"),
                    icon: "trash",
                    danger: true,
                  },
                ]}
              />
            </>
          }
          aside={
            <>
              <ProfileBlock title={t("agent.usage")} level={4}>
                <UsageLimit
                  label={t("agent.runsMonth")}
                  used={412}
                  limit={500}
                  valueText={t("agent.runsMonthValue")}
                  resetText={t("agent.resets")}
                />
              </ProfileBlock>
              <ProfileBlock title={t("agent.access")} level={4}>
                <FactList
                  facts={[
                    {
                      key: "who",
                      label: t("agent.whoCanUse"),
                      value: t("agent.whoCanUseValue"),
                    },
                    {
                      key: "data",
                      label: t("agent.data"),
                      value: t("agent.dataValue"),
                    },
                    {
                      key: "review",
                      label: t("agent.review"),
                      value: t("agent.reviewValue"),
                    },
                  ]}
                />
              </ProfileBlock>
            </>
          }
        >
          <Tabs
            label={t("agent.sections")}
            tabs={[
              {
                key: "overview",
                label: t("agent.overview"),
                content: (
                  <StatCards
                    label={t("agent.numbers")}
                    stats={[
                      {
                        key: "runs",
                        label: t("agent.runs"),
                        value: n(412),
                        change: 0.18,
                        period: t("dashboard.lastMonth"),
                      },
                      {
                        key: "success",
                        label: t("agent.success"),
                        value: t("agent.successValue"),
                        change: 0.03,
                        period: t("dashboard.lastMonth"),
                      },
                      {
                        key: "time",
                        label: t("agent.time"),
                        value: t("agent.timeValue"),
                        change: -0.1,
                        goodWhen: "down",
                        period: t("dashboard.lastMonth"),
                      },
                    ]}
                  />
                ),
              },
              {
                key: "runs",
                label: t("agent.runsTab"),
                content: (
                  <DataTable
                    caption={t("agent.runsCaption")}
                    columns={runColumns}
                    rows={runs}
                    rowKey={(r) => r.id}
                    density="compact"
                  />
                ),
              },
            ]}
          />
        </ProfileLayout>
      </section>

      <section aria-labelledby="pt-record">
        <h2 id="pt-record">{t("record.title")}</h2>
        <p className="notice">{t("record.intro")}</p>
        <ProfileLayout
          level={3}
          media={<ProfileIcon icon="inbox" />}
          eyebrow={t("record.number")}
          title={t("record.name")}
          status={{ text: t("record.status"), tone: "info", icon: "clock" }}
          facts={[
            {
              key: "requester",
              label: t("record.requester"),
              value: t("people.sara"),
            },
            {
              key: "assignee",
              label: t("record.assignee"),
              value: t("people.omar"),
            },
            { key: "due", label: t("record.due"), value: t("record.dueValue") },
          ]}
          actions={
            <>
              <Button variant="primary" icon="check">
                {t("record.approve")}
              </Button>
              <Button icon="user">{t("record.reassign")}</Button>
            </>
          }
          aside={
            <>
              <ProfileBlock title={t("record.people")} level={4}>
                <ul className="pt-people">
                  {[t("people.sara"), t("people.omar"), t("people.huda")].map(
                    (name, i) => (
                      <li key={name}>
                        <Avatar name={name} size="sm" />
                        <span>
                          <bdi>{name}</bdi>
                          <span>
                            {t(
                              [
                                "record.roleRequester",
                                "record.roleAssignee",
                                "record.roleApprover",
                              ][i] ?? "record.roleRequester",
                            )}
                          </span>
                        </span>
                      </li>
                    ),
                  )}
                </ul>
              </ProfileBlock>
              <ProfileBlock title={t("record.related")} level={4}>
                <FactList
                  facts={[
                    {
                      key: "service",
                      label: t("record.service"),
                      value: t("record.serviceValue"),
                    },
                    {
                      key: "asset",
                      label: t("record.asset"),
                      value: t("record.assetValue"),
                    },
                  ]}
                />
              </ProfileBlock>
            </>
          }
        >
          <Tabs
            label={t("record.sections")}
            tabs={[
              {
                key: "details",
                label: t("record.details"),
                content: (
                  <FactList
                    facts={[
                      {
                        key: "model",
                        label: t("record.model"),
                        value: t("record.modelValue"),
                      },
                      {
                        key: "reason",
                        label: t("record.reason"),
                        value: t("record.reasonValue"),
                      },
                      {
                        key: "cost",
                        label: t("record.cost"),
                        value: t("record.costValue"),
                      },
                      {
                        key: "location",
                        label: t("record.location"),
                        value: t("record.locationValue"),
                      },
                    ]}
                  />
                ),
              },
              {
                key: "activity",
                label: t("record.activity"),
                content: (
                  <Timeline
                    label={t("record.activity")}
                    entries={[
                      {
                        id: "a3",
                        actor: t("people.omar"),
                        text: t("record.event3"),
                        time: t("record.time3"),
                        dateTime: "2026-11-10T09:30",
                      },
                      {
                        id: "a2",
                        actor: t("people.huda"),
                        text: t("record.event2"),
                        time: t("record.time2"),
                        dateTime: "2026-11-09T15:10",
                      },
                      {
                        id: "a1",
                        icon: "plus",
                        text: t("record.event1"),
                        time: t("record.time1"),
                        dateTime: "2026-11-09T08:45",
                      },
                    ]}
                  />
                ),
              },
            ]}
          />
        </ProfileLayout>
      </section>

      <section aria-labelledby="pt-board">
        <h2 id="pt-board">{t("board.title")}</h2>
        <p className="notice">{t("board.intro")}</p>
        <Board
          label={t("board.label")}
          level={3}
          columns={columns}
          cards={cards}
          onMove={(id, column) =>
            setCards((list) => [
              ...list.filter((x) => x.id !== id),
              { ...list.find((x) => x.id === id)!, column },
            ])
          }
        />
      </section>
    </div>
  );
}
