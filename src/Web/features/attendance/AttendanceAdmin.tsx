"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, SelectField } from "@/components/ui/FormField";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { problemMessages } from "@/lib/notifications/types";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import type { LeaveType } from "@/lib/leave";
import { pick } from "@/lib/services/types";
import { thisMonth, type AttendanceSettings, type AttendanceSite, type LocationMode, type WorkRule } from "@/lib/attendance";
import "./attendance.css";

type SiteDraft = { name: string; latitude: string; longitude: string; radiusMeters: string };

const toDraft = (s: AttendanceSite): SiteDraft => ({ name: s.name, latitude: String(s.latitude), longitude: String(s.longitude), radiusMeters: String(s.radiusMeters) });

/**
 * Attendance settings (spec 022 FR-010, US3 scenario 1): whether people sign in on the web, the optional location check
 * against named sites, the working hours rules per unit, and the monthly export for payroll. The API decides who may
 * change what (the tenant settings need an HR officer of the whole organization) and validates every value.
 */
export function AttendanceAdmin({
  settings: initialSettings,
  rules: initialRules,
  units,
  leaveTypes,
}: {
  settings: AttendanceSettings;
  rules: WorkRule[];
  units: OrgUnit[];
  leaveTypes: LeaveType[];
}) {
  const t = useTranslations("attendance.admin");
  const locale = useLocale();
  const [settings, setSettings] = useState(initialSettings);
  const [web, setWeb] = useState(initialSettings.webSignIn);
  const [mode, setMode] = useState<LocationMode>(initialSettings.locationMode);
  const [sites, setSites] = useState<SiteDraft[]>(initialSettings.sites.map(toDraft));
  const [compensation, setCompensation] = useState(initialSettings.compensationTypeKey ?? "");
  const [rules, setRules] = useState(initialRules);
  const [unit, setUnit] = useState("");
  const [start, setStart] = useState("09:00");
  const [end, setEnd] = useState("17:00");
  const [late, setLate] = useState("15");
  const [early, setEarly] = useState("15");
  const [month, setMonth] = useState(thisMonth());
  const [busy, setBusy] = useState<"settings" | "rule" | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  const unitLabel = (id: string | null) => (id ? (units.find((u) => u.id === id) ? unitName(units.find((u) => u.id === id)!, locale) : id) : t("wholeOrganization"));

  async function run(kind: "settings" | "rule", action: () => Promise<Response>, done: (response: Response) => Promise<void>) {
    setBusy(kind);
    setNotice(null);
    setMessages([]);
    try {
      const response = await action();
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return;
      }
      await done(response);
      setNotice(t("saved"));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  function saveSettings() {
    void run(
      "settings",
      () =>
        fetch("/api/v1/hr/attendance-settings", {
          method: "PUT",
          headers: { "content-type": "application/json", "If-Match": settings.eTag },
          body: JSON.stringify({
            webSignIn: web,
            locationMode: mode,
            sites: sites.map((s) => ({ name: s.name, latitude: Number(s.latitude), longitude: Number(s.longitude), radiusMeters: Number(s.radiusMeters) })),
            compensationTypeKey: compensation || null,
          }),
        }),
      async (response) => {
        const next = (await response.json()) as AttendanceSettings;
        setSettings(next);
        setWeb(next.webSignIn);
        setMode(next.locationMode);
        setSites(next.sites.map(toDraft));
        setCompensation(next.compensationTypeKey ?? "");
      },
    );
  }

  function saveRule() {
    const orgUnitId = unit || null;
    const existing = rules.find((r) => r.orgUnitId === orgUnitId);
    void run(
      "rule",
      () =>
        fetch("/api/v1/hr/work-rules", {
          method: "PUT",
          headers: { "content-type": "application/json", ...(existing ? { "If-Match": existing.eTag } : {}) },
          body: JSON.stringify({ orgUnitId, start, end, graceLateMinutes: Number(late), graceEarlyMinutes: Number(early) }),
        }),
      async (response) => {
        const saved = (await response.json()) as WorkRule;
        setRules((list) => [...list.filter((r) => r.id !== saved.id), saved]);
      },
    );
  }

  function edit(rule: WorkRule) {
    setUnit(rule.orgUnitId ?? "");
    setStart(rule.start);
    setEnd(rule.end);
    setLate(String(rule.graceLateMinutes));
    setEarly(String(rule.graceEarlyMinutes));
  }

  const setSite = (i: number, patch: Partial<SiteDraft>) => setSites((list) => list.map((s, j) => (j === i ? { ...s, ...patch } : s)));
  const locked = !settings.canChange || busy !== null;

  return (
    <div className="attendance">
      {notice ? (
        <p className="notice" role="status">
          {notice}
        </p>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}

      <section className="attendance-section" aria-labelledby="attendance-settings">
        <h2 id="attendance-settings">{t("settings.title")}</h2>
        {!settings.canChange ? <p className="field-hint">{t("settings.readOnly")}</p> : null}
        <Checkbox label={t("settings.webSignIn")} hint={t("settings.webSignInHint")} name="web-sign-in" checked={web} disabled={locked} onChange={(e) => setWeb(e.target.checked)} />
        <RadioGroup
          legend={t("settings.location")}
          name="location-mode"
          value={mode}
          onChange={(e) => setMode(e.target.value as LocationMode)}
          options={(["off", "flag", "refuse"] as const).map((m) => ({ value: m, label: t(`settings.modes.${m}`), hint: t(`settings.modeHints.${m}`), disabled: locked }))}
        />
        <h3>{t("settings.sites")}</h3>
        <p className="field-hint">{t("settings.sitesHint")}</p>
        {sites.map((s, i) => (
          <div className="attendance-site" key={i} role="group" aria-label={t("settings.site", { n: i + 1 })}>
            <FormField label={t("settings.siteName")} name={`site-name-${i}`} value={s.name} maxLength={80} disabled={locked} onChange={(e) => setSite(i, { name: e.target.value })} />
            <FormField label={t("settings.latitude")} name={`site-lat-${i}`} inputMode="decimal" dir="ltr" value={s.latitude} disabled={locked} onChange={(e) => setSite(i, { latitude: e.target.value })} />
            <FormField label={t("settings.longitude")} name={`site-lon-${i}`} inputMode="decimal" dir="ltr" value={s.longitude} disabled={locked} onChange={(e) => setSite(i, { longitude: e.target.value })} />
            <FormField label={t("settings.radius")} name={`site-radius-${i}`} inputMode="numeric" dir="ltr" value={s.radiusMeters} disabled={locked} onChange={(e) => setSite(i, { radiusMeters: e.target.value })} />
            <Button size="sm" variant="quiet" disabled={locked} onClick={() => setSites((list) => list.filter((_, j) => j !== i))}>
              {t("settings.removeSite")}
            </Button>
          </div>
        ))}
        <SelectField
          label={t("settings.compensation")}
          hint={t("settings.compensationHint")}
          name="compensation-type"
          value={compensation}
          disabled={locked}
          onChange={(e) => setCompensation(e.target.value)}
          options={[
            { value: "", label: t("settings.compensationOff") },
            ...leaveTypes.filter((x) => x.active && x.unit === "day").map((x) => ({ value: x.key, label: pick(x.names, locale, x.key) })),
          ]}
        />
        {settings.canChange ? (
          <div className="attendance-row">
            <Button size="sm" icon="plus" disabled={locked} onClick={() => setSites((list) => [...list, { name: "", latitude: "", longitude: "", radiusMeters: "200" }])}>
              {t("settings.addSite")}
            </Button>
            <Button variant="primary" busy={busy === "settings"} busyLabel={t("saving")} disabled={busy !== null} onClick={saveSettings}>
              {t("settings.save")}
            </Button>
          </div>
        ) : null}
      </section>

      <section className="attendance-section" aria-labelledby="attendance-rules">
        <h2 id="attendance-rules">{t("rules.title")}</h2>
        <p className="field-hint">{t("rules.intro")}</p>
        <DataTable<WorkRule>
          caption={t("rules.caption")}
          rows={rules}
          rowKey={(r) => r.id}
          density="compact"
          emptyTitle={t("rules.empty")}
          columns={[
            { key: "unit", header: t("rules.unit"), rowHeader: true, sortValue: (r) => unitLabel(r.orgUnitId), cell: (r) => <bdi>{unitLabel(r.orgUnitId)}</bdi> },
            { key: "hours", header: t("rules.hours"), cell: (r) => <bdi>{t("rules.hoursValue", { start: r.start, end: r.end })}</bdi> },
            { key: "grace", header: t("rules.grace"), cell: (r) => t("rules.graceValue", { late: r.graceLateMinutes, early: r.graceEarlyMinutes }) },
            {
              key: "edit",
              header: t("rules.actions"),
              cell: (r) => (
                <Button size="xs" onClick={() => edit(r)}>
                  {t("rules.edit")}
                </Button>
              ),
            },
          ]}
        />
        <div className="attendance-row">
          <SelectField
            label={t("rules.unit")}
            name="rule-unit"
            value={unit}
            onChange={(e) => setUnit(e.target.value)}
            options={[{ value: "", label: t("wholeOrganization") }, ...units.map((u) => ({ value: u.id, label: unitName(u, locale) }))]}
          />
          <FormField label={t("rules.start")} name="rule-start" type="time" value={start} onChange={(e) => setStart(e.target.value)} />
          <FormField label={t("rules.end")} name="rule-end" type="time" value={end} onChange={(e) => setEnd(e.target.value)} />
          <FormField label={t("rules.graceLate")} name="rule-late" inputMode="numeric" value={late} onChange={(e) => setLate(e.target.value)} />
          <FormField label={t("rules.graceEarly")} name="rule-early" inputMode="numeric" value={early} onChange={(e) => setEarly(e.target.value)} />
          <Button variant="primary" busy={busy === "rule"} busyLabel={t("saving")} disabled={busy !== null} onClick={saveRule}>
            {t("rules.save")}
          </Button>
        </div>
      </section>

      <section className="attendance-section" aria-labelledby="attendance-export">
        <h2 id="attendance-export">{t("export.title")}</h2>
        <p className="field-hint">{t("export.intro")}</p>
        <div className="attendance-row">
          <FormField label={t("export.month")} name="export-month" type="month" value={month} onChange={(e) => e.target.value && setMonth(e.target.value)} />
          <ButtonLink href={`/api/v1/hr/attendance/export?month=${month}`} icon="download" download>
            {t("export.download")}
          </ButtonLink>
        </div>
      </section>
    </div>
  );
}
