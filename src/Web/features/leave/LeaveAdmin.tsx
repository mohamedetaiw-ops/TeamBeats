"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField } from "@/components/ui/FormField";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { problemMessages } from "@/lib/notifications/types";
import { pick } from "@/lib/services/types";
import type { LeaveBand, LeavePolicy, LeaveType } from "@/lib/leave";
import "./leave.css";

type Run = (call: () => Promise<Response>, done: string) => Promise<boolean>;

/**
 * Leave admin (spec 022 FR-001): leave types and entitlement policies. The API validates every value, keeps versions
 * and checks the If-Match tag so two editors never overwrite each other; this page only collects the values.
 */
export function LeaveAdmin({ initialTypes, initialPolicies, units }: { initialTypes: LeaveType[]; initialPolicies: LeavePolicy[]; units: OrgUnit[] }) {
  const t = useTranslations("leave.admin");
  const locale = useLocale();
  const [types, setTypes] = useState(initialTypes);
  const [policies, setPolicies] = useState(initialPolicies);
  const [editing, setEditing] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function reload() {
    const [a, b] = await Promise.all([fetch("/api/v1/hr/leave/types?all=true", { cache: "no-store" }), fetch("/api/v1/hr/leave/policies", { cache: "no-store" })]);
    if (a.ok) setTypes((await a.json()) as LeaveType[]);
    if (b.ok) setPolicies((await b.json()) as LeavePolicy[]);
  }

  const run: Run = async (call, done) => {
    setMessages([]);
    setNotice(null);
    try {
      const response = await call();
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return false;
      }
      setNotice(done);
      await reload();
      return true;
    } catch {
      setMessages([t("failed")]);
      return false;
    }
  };

  const unitLabel = (id: string | null) => {
    if (!id) return t("wholeOrganization");
    const unit = units.find((u) => u.id === id);
    return unit ? unitName(unit, locale) : id;
  };

  return (
    <div className="leave">
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

      <section className="leave-section" aria-labelledby="leave-types">
        <h2 id="leave-types">{t("types.title")}</h2>
        <ul className="leave-list">
          {types.map((x) => (
            <li key={x.key} className="leave-item">
              <span>
                <bdi>{pick(x.names, locale, x.key)}</bdi> <span className="field-hint">{x.key}</span>{" "}
                {!x.active ? <Badge>{t("types.inactive")}</Badge> : null}
                {x.sensitive ? <Badge tone="info">{t("types.sensitive")}</Badge> : null}
              </span>
              <Button size="sm" onClick={() => setEditing(editing === x.key ? null : x.key)} aria-expanded={editing === x.key}>
                {t("types.edit")}
              </Button>
              {editing === x.key ? <TypeForm type={x} run={run} onSaved={() => setEditing(null)} /> : null}
            </li>
          ))}
        </ul>
        <NewType run={run} />
      </section>

      <section className="leave-section" aria-labelledby="leave-policies">
        <h2 id="leave-policies">{t("policies.title")}</h2>
        <p className="field-hint">{t("policies.intro")}</p>
        <ul className="leave-list">
          {policies.map((p) => (
            <li key={p.id} className="leave-item">
              <span>
                <bdi>{pick(types.find((x) => x.key === p.typeKey)?.names, locale, p.typeKey)}</bdi> · <bdi>{unitLabel(p.orgUnitId)}</bdi> ·{" "}
                {t(`policies.accrual.${p.accrual}`)}
              </span>
              <PolicyForm policy={p} types={types} units={units} run={run} />
            </li>
          ))}
        </ul>
        <h3>{t("policies.new")}</h3>
        <PolicyForm policy={null} types={types} units={units} run={run} />
      </section>
    </div>
  );
}

function TypeForm({ type, run, onSaved }: { type: LeaveType; run: Run; onSaved: () => void }) {
  const t = useTranslations("leave.admin");
  const [value, setValue] = useState(type);
  const set = <K extends keyof LeaveType>(k: K, v: LeaveType[K]) => setValue((s) => ({ ...s, [k]: v }));
  const num = (s: string) => (s.trim() === "" ? null : Number(s));
  return (
    <form
      className="leave-form"
      onSubmit={(e) => {
        e.preventDefault();
        void run(
          () =>
            fetch(`/api/v1/hr/leave/types/${encodeURIComponent(type.key)}`, {
              method: "PUT",
              headers: { "content-type": "application/json", "If-Match": type.eTag },
              body: JSON.stringify({ ...value, membershipTypes: value.membershipTypes?.length ? value.membershipTypes : null }),
            }),
          t("types.saved"),
        ).then((ok) => ok && onSaved());
      }}
    >
      <div className="ui-control-row">
        <FormField label={t("types.nameEn")} name="nameEn" value={value.names.en ?? ""} onChange={(e) => set("names", { ...value.names, en: e.target.value })} required />
        <FormField label={t("types.nameAr")} name="nameAr" dir="rtl" value={value.names.ar ?? ""} onChange={(e) => set("names", { ...value.names, ar: e.target.value })} />
      </div>
      <div className="ui-control-row">
        <SelectField
          label={t("types.unit")}
          name="unit"
          value={value.unit}
          onChange={(e) => set("unit", e.target.value as LeaveType["unit"])}
          options={[
            { value: "day", label: t("types.units.day") },
            { value: "hour", label: t("types.units.hour") },
          ]}
        />
        <FormField label={t("types.noticeDays")} name="noticeDays" type="number" min={0} value={value.noticeDays} onChange={(e) => set("noticeDays", Number(e.target.value))} />
        <FormField
          label={t("types.maxDays")}
          name="maxDays"
          type="number"
          min={0}
          step="0.5"
          value={value.maxDaysPerRequest ?? ""}
          onChange={(e) => set("maxDaysPerRequest", num(e.target.value))}
        />
        <FormField
          label={t("types.documentAfter")}
          name="documentAfter"
          type="number"
          min={0}
          step="0.5"
          value={value.documentAfterDays ?? ""}
          onChange={(e) => set("documentAfterDays", num(e.target.value))}
        />
      </div>
      <div className="ui-control-row">
        <FormField label={t("types.carryOver")} name="carryOver" type="number" min={0} step="0.5" value={value.carryOverMax} onChange={(e) => set("carryOverMax", Number(e.target.value))} />
        <FormField label={t("types.negativeMax")} name="negativeMax" type="number" min={0} step="0.5" value={value.negativeMax} onChange={(e) => set("negativeMax", Number(e.target.value))} />
        <FormField label={t("types.yearStart")} name="yearStart" type="number" min={1} max={12} value={value.yearStartMonth} onChange={(e) => set("yearStartMonth", Number(e.target.value))} />
        <FormField
          label={t("types.membership")}
          name="membership"
          hint={t("types.membershipHint")}
          value={(value.membershipTypes ?? []).join(", ")}
          onChange={(e) =>
            set(
              "membershipTypes",
              e.target.value
                .split(",")
                .map((s) => s.trim())
                .filter(Boolean),
            )
          }
        />
      </div>
      <div className="ui-control-row">
        <Checkbox label={t("types.halfDays")} name="halfDays" checked={value.halfDays} onChange={(e) => set("halfDays", e.target.checked)} />
        <Checkbox label={t("types.paid")} name="paid" checked={value.paid} onChange={(e) => set("paid", e.target.checked)} />
        <Checkbox label={t("types.sensitive")} hint={t("types.sensitiveHint")} name="sensitive" checked={value.sensitive} onChange={(e) => set("sensitive", e.target.checked)} />
        <Checkbox label={t("types.negative")} name="negative" checked={value.negativeAllowed} onChange={(e) => set("negativeAllowed", e.target.checked)} />
        <Checkbox label={t("types.cancelApproval")} name="cancelApproval" checked={value.cancelNeedsApproval} onChange={(e) => set("cancelNeedsApproval", e.target.checked)} />
        <Checkbox label={t("types.active")} name="active" checked={value.active} onChange={(e) => set("active", e.target.checked)} />
      </div>
      <div className="leave-actions">
        <Button type="submit" variant="primary">
          {t("types.save")}
        </Button>
      </div>
    </form>
  );
}

function NewType({ run }: { run: Run }) {
  const t = useTranslations("leave.admin");
  const [key, setKey] = useState("");
  const [nameEn, setNameEn] = useState("");
  const [nameAr, setNameAr] = useState("");
  return (
    <form
      className="leave-form"
      onSubmit={(e) => {
        e.preventDefault();
        void run(
          () =>
            fetch(`/api/v1/hr/leave/types/${encodeURIComponent(key.trim())}`, {
              method: "PUT",
              headers: { "content-type": "application/json" },
              body: JSON.stringify({ names: { en: nameEn, ...(nameAr ? { ar: nameAr } : {}) } }),
            }),
          t("types.created"),
        ).then((ok) => {
          if (ok) {
            setKey("");
            setNameEn("");
            setNameAr("");
          }
        });
      }}
    >
      <h3>{t("types.new")}</h3>
      <div className="ui-control-row">
        <FormField label={t("types.key")} name="key" hint={t("types.keyHint")} value={key} onChange={(e) => setKey(e.target.value)} required />
        <FormField label={t("types.nameEn")} name="newNameEn" value={nameEn} onChange={(e) => setNameEn(e.target.value)} required />
        <FormField label={t("types.nameAr")} name="newNameAr" dir="rtl" value={nameAr} onChange={(e) => setNameAr(e.target.value)} />
      </div>
      <div className="leave-actions">
        <Button type="submit">{t("types.add")}</Button>
      </div>
    </form>
  );
}

type BandText = { days: string; years: string; grades: string };

const toText = (b: LeaveBand): BandText => ({ days: String(b.days), years: b.minServiceYears == null ? "" : String(b.minServiceYears), grades: (b.grades ?? []).join(", ") });

function PolicyForm({ policy, types, units, run }: { policy: LeavePolicy | null; types: LeaveType[]; units: OrgUnit[]; run: Run }) {
  const t = useTranslations("leave.admin");
  const locale = useLocale();
  const [typeKey, setTypeKey] = useState(policy?.typeKey ?? types[0]?.key ?? "");
  const [unit, setUnit] = useState(policy?.orgUnitId ?? "");
  const [accrual, setAccrual] = useState(policy?.accrual ?? "yearly");
  const [bands, setBands] = useState<BandText[]>(policy ? policy.bands.map(toText) : [{ days: "21", years: "", grades: "" }]);
  if (policy && !policy.canEdit) return null;
  const body = () =>
    JSON.stringify({
      typeKey,
      orgUnitId: unit || null,
      accrual,
      bands: bands.map((b) => ({
        days: Number(b.days),
        minServiceYears: b.years.trim() ? Number(b.years) : null,
        grades: b.grades.trim()
          ? b.grades
              .split(",")
              .map((s) => s.trim())
              .filter(Boolean)
          : null,
      })),
    });
  const setBand = (i: number, k: keyof BandText, v: string) => setBands((list) => list.map((b, j) => (j === i ? { ...b, [k]: v } : b)));
  return (
    <form
      className="leave-form"
      onSubmit={(e) => {
        e.preventDefault();
        void run(
          () =>
            policy
              ? fetch(`/api/v1/hr/leave/policies/${policy.id}`, { method: "PUT", headers: { "content-type": "application/json", "If-Match": policy.eTag }, body: body() })
              : fetch("/api/v1/hr/leave/policies", { method: "POST", headers: { "content-type": "application/json" }, body: body() }),
          t("policies.saved"),
        );
      }}
    >
      {policy ? null : (
        <div className="ui-control-row">
          <SelectField label={t("policies.type")} name="typeKey" value={typeKey} onChange={(e) => setTypeKey(e.target.value)} options={types.map((x) => ({ value: x.key, label: pick(x.names, locale, x.key) }))} />
          <SelectField
            label={t("policies.unit")}
            name="orgUnitId"
            value={unit}
            onChange={(e) => setUnit(e.target.value)}
            options={[{ value: "", label: t("policies.wholeOrganization") }, ...units.map((u) => ({ value: u.id, label: unitName(u, locale) }))]}
          />
        </div>
      )}
      <SelectField
        label={t("policies.accrualLabel")}
        name="accrual"
        value={accrual}
        onChange={(e) => setAccrual(e.target.value as LeavePolicy["accrual"])}
        options={[
          { value: "yearly", label: t("policies.accrual.yearly") },
          { value: "monthly", label: t("policies.accrual.monthly") },
        ]}
      />
      <fieldset className="leave-plain">
        <legend>{t("policies.bands")}</legend>
        <p className="field-hint">{t("policies.bandsHint")}</p>
        {bands.map((b, i) => (
          <div key={i} className="ui-control-row">
            <FormField label={t("policies.days")} name={`days-${i}`} type="number" min={0} step="0.5" value={b.days} onChange={(e) => setBand(i, "days", e.target.value)} required />
            <FormField label={t("policies.years")} name={`years-${i}`} type="number" min={0} value={b.years} onChange={(e) => setBand(i, "years", e.target.value)} />
            <FormField label={t("policies.grades")} name={`grades-${i}`} value={b.grades} onChange={(e) => setBand(i, "grades", e.target.value)} />
            <Button size="sm" variant="quiet" disabled={bands.length === 1} onClick={() => setBands((list) => list.filter((_, j) => j !== i))}>
              {t("policies.removeBand")}
            </Button>
          </div>
        ))}
        <div className="leave-actions">
          <Button size="sm" icon="plus" onClick={() => setBands((list) => [...list, { days: "", years: "", grades: "" }])}>
            {t("policies.addBand")}
          </Button>
        </div>
      </fieldset>
      <div className="leave-actions">
        <Button type="submit" variant="primary">
          {policy ? t("policies.save") : t("policies.create")}
        </Button>
        {policy ? (
          <Button variant="destructive" onClick={() => void run(() => fetch(`/api/v1/hr/leave/policies/${policy.id}`, { method: "DELETE" }), t("policies.deleted"))}>
            {t("policies.delete")}
          </Button>
        ) : null}
      </div>
    </form>
  );
}
