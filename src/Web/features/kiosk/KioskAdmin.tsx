"use client";

import { useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, SelectField } from "@/components/ui/FormField";
import { NumberField } from "@/components/ui/Inputs";
import { PersonPicker } from "@/features/services/PersonPicker";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { kioskCall, kioskFeatures, type Badge as KioskBadge, type BadgeIssued, type KioskDevice, type KioskEnrolment, type KioskFeature } from "@/lib/kiosk";
import "./kiosk.css";

const tones = { waiting: "warning", enrolled: "success", revoked: "neutral" } as const;

/**
 * Kiosks and badges for device administrators (spec 030 US4, FR-014, FR-015). A new kiosk or a replacement tablet gets
 * a one-time code shown once; a badge's code and QR are shown once for printing. Every change is checked by the API.
 */
export function KioskAdmin({ initial, units }: { initial: KioskDevice[]; units: OrgUnit[] }) {
  const t = useTranslations("kiosk.admin");
  const [devices, setDevices] = useState(initial);
  const [enrolment, setEnrolment] = useState<KioskEnrolment | null>(null);

  const replace = (device: KioskDevice) => setDevices((list) => list.map((d) => (d.id === device.id ? device : d)));

  return (
    <div className="kiosk-start-admin">
      {enrolment ? <EnrolmentCode enrolment={enrolment} onDone={() => setEnrolment(null)} /> : null}
      <section aria-labelledby="kiosk-list-title">
        <h2 id="kiosk-list-title">{t("listTitle")}</h2>
        {devices.length === 0 ? (
          <p className="field-hint">{t("empty")}</p>
        ) : (
          <ul className="kiosk-list">
            {devices.map((device) => (
              <li key={device.id}>
                <DeviceItem
                  device={device}
                  units={units}
                  onChanged={replace}
                  onCode={(e) => {
                    replace(e.device);
                    setEnrolment(e);
                  }}
                />
              </li>
            ))}
          </ul>
        )}
      </section>
      <CreateKiosk
        units={units}
        onCreated={(e) => {
          setDevices((list) => [...list, e.device]);
          setEnrolment(e);
        }}
      />
      <Badges />
    </div>
  );
}

function EnrolmentCode({ enrolment, onDone }: { enrolment: KioskEnrolment; onDone: () => void }) {
  const t = useTranslations("kiosk.admin");
  const locale = useLocale();
  const until = new Intl.DateTimeFormat(locale, { timeStyle: "short" }).format(new Date(enrolment.expiresAt));
  return (
    <Alert tone="success" title={t("codeTitle", { name: enrolment.device.name })} live action={<Button size="sm" variant="secondary" onClick={onDone}>{t("codeDone")}</Button>}>
      <p className="kiosk-code">{enrolment.code}</p>
      <p>{t("codeHow", { time: until })}</p>
    </Alert>
  );
}

function DeviceItem({
  device,
  units,
  onChanged,
  onCode,
}: {
  device: KioskDevice;
  units: OrgUnit[];
  onChanged: (device: KioskDevice) => void;
  onCode: (enrolment: KioskEnrolment) => void;
}) {
  const t = useTranslations("kiosk.admin");
  const locale = useLocale();
  const [editing, setEditing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const unit = units.find((u) => u.id === device.orgUnitId);
  const date = (iso: string | null) => (iso ? new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(iso)) : null);

  async function act(path: string) {
    setError(null);
    const result = await kioskCall<KioskDevice | KioskEnrolment>(`/api/v1/identity/kiosks/${device.id}${path}`, "POST");
    if (!result.ok) {
      setError(result.refusal.messages[0] ?? t("failed"));
      return;
    }
    if ("code" in result.value) onCode(result.value);
    else onChanged(result.value);
  }

  return (
    <article className="kiosk-item" aria-labelledby={`kiosk-${device.id}`}>
      <div className="kiosk-item-head">
        <h3 id={`kiosk-${device.id}`}>{device.name}</h3>
        <Badge tone={tones[device.status]}>{t(`status.${device.status}`)}</Badge>
      </div>
      <p className="field-hint">
        {[unit ? unitName(unit, locale) : null, device.location].filter(Boolean).join(" · ")}
        {" · "}
        {t("idle", { minutes: device.idleMinutes })}
      </p>
      <p className="field-hint">{device.features.length > 0 ? device.features.map((f) => t(`feature.${f}`)).join(", ") : t("homeOnly")}</p>
      {device.lastSeenAt ? <p className="field-hint">{t("lastSeen", { time: date(device.lastSeenAt)! })}</p> : null}
      {error ? (
        <Alert tone="danger" title={t("failed")} live>
          {error}
        </Alert>
      ) : null}
      {device.status !== "revoked" ? (
        <div className="kiosk-actions">
          <Button size="sm" variant="secondary" onClick={() => setEditing((v) => !v)} aria-expanded={editing}>
            {t("edit")}
          </Button>
          <Button size="sm" variant="secondary" onClick={() => void act(":enrol-code")}>
            {device.status === "waiting" ? t("newCode") : t("replaceTablet")}
          </Button>
          <ConfirmAction
            label={t("revoke")}
            question={t("revokeQuestion", { name: device.name })}
            confirmLabel={t("revokeConfirm")}
            cancelLabel={t("cancel")}
            onConfirm={() => act(":revoke")}
          />
        </div>
      ) : null}
      {editing ? (
        <KioskForm
          units={units}
          initial={device}
          submitLabel={t("save")}
          onSubmit={async (body) => {
            const result = await kioskCall<KioskDevice>(`/api/v1/identity/kiosks/${device.id}`, "PUT", { ...body, version: device.version });
            if (result.ok) {
              onChanged(result.value);
              setEditing(false);
              return null;
            }
            return result.refusal.messages[0] ?? t("failed");
          }}
        />
      ) : null}
    </article>
  );
}

type KioskBody = { name: string; orgUnitId: string; location: string; features: KioskFeature[]; idleMinutes: number; allowOtherUnits: boolean };

function CreateKiosk({ units, onCreated }: { units: OrgUnit[]; onCreated: (enrolment: KioskEnrolment) => void }) {
  const t = useTranslations("kiosk.admin");
  return (
    <section className="kiosk-panel" aria-labelledby="kiosk-create-title">
      <h2 id="kiosk-create-title">{t("createTitle")}</h2>
      <p className="field-hint">{t("createIntro")}</p>
      <KioskForm
        units={units}
        submitLabel={t("create")}
        onSubmit={async (body) => {
          const result = await kioskCall<KioskEnrolment>("/api/v1/identity/kiosks", "POST", body);
          if (result.ok) {
            onCreated(result.value);
            return null;
          }
          return result.refusal.messages[0] ?? t("failed");
        }}
      />
    </section>
  );
}

function KioskForm({
  units,
  initial,
  submitLabel,
  onSubmit,
}: {
  units: OrgUnit[];
  initial?: KioskDevice;
  submitLabel: string;
  onSubmit: (body: KioskBody) => Promise<string | null>;
}) {
  const t = useTranslations("kiosk.admin");
  const locale = useLocale();
  const [name, setName] = useState(initial?.name ?? "");
  const [unit, setUnit] = useState(initial?.orgUnitId ?? units[0]?.id ?? "");
  const [location, setLocation] = useState(initial?.location ?? "");
  const [features, setFeatures] = useState<KioskFeature[]>(initial?.features ?? kioskFeatures);
  const [idle, setIdle] = useState<number | null>(initial?.idleMinutes ?? 2);
  const [others, setOthers] = useState(initial?.allowOtherUnits ?? false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    const failed = await onSubmit({ name, orgUnitId: unit, location, features, idleMinutes: idle ?? 2, allowOtherUnits: others });
    setBusy(false);
    setError(failed);
    if (!failed && !initial) {
      setName("");
      setLocation("");
    }
  }

  return (
    <form className="kiosk-form" onSubmit={(e) => void submit(e)} noValidate>
      {error ? (
        <Alert tone="danger" title={t("failed")} live>
          {error}
        </Alert>
      ) : null}
      <FormField label={t("name")} name="name" value={name} onChange={(e) => setName(e.target.value)} maxLength={100} required />
      <SelectField
        label={t("unit")}
        name="unit"
        hint={t("unitHint")}
        value={unit}
        onChange={(e) => setUnit(e.target.value)}
        options={units.map((u) => ({ value: u.id, label: unitName(u, locale) }))}
      />
      <FormField label={t("location")} name="location" hint={t("locationHint")} value={location} onChange={(e) => setLocation(e.target.value)} maxLength={100} />
      <fieldset className="kiosk-features">
        <legend>{t("features")}</legend>
        {kioskFeatures.map((f) => (
          <Checkbox
            key={f}
            name={`feature-${f}`}
            label={t(`feature.${f}`)}
            checked={features.includes(f)}
            onChange={(e) => setFeatures((list) => (e.target.checked ? [...list, f] : list.filter((x) => x !== f)))}
          />
        ))}
      </fieldset>
      <p className="field-hint">{t("featuresHint")}</p>
      <NumberField label={t("idleMinutes")} name="idleMinutes" hint={t("idleHint")} value={idle} onValueChange={setIdle} min={1} max={15} unit={t("minutes")} />
      <Checkbox name="allowOtherUnits" label={t("allowOtherUnits")} hint={t("allowOtherUnitsHint")} checked={others} onChange={(e) => setOthers(e.target.checked)} />
      <div className="kiosk-actions">
        <Button type="submit" busy={busy} disabled={name.trim().length === 0 || !unit}>
          {submitLabel}
        </Button>
      </div>
    </form>
  );
}

/** Badges: issue one for a person, print its QR and code, or clear a forgotten PIN. */
function Badges() {
  const t = useTranslations("kiosk.admin");
  const [person, setPerson] = useState<string | null>(null);
  const [badge, setBadge] = useState<KioskBadge | null>(null);
  const [issued, setIssued] = useState<BadgeIssued | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function pick(id: string | null) {
    setPerson(id);
    setBadge(null);
    setIssued(null);
    setError(null);
    if (!id) return;
    const result = await kioskCall<KioskBadge>(`/api/v1/identity/badges/${id}`, "GET");
    if (result.ok) setBadge(result.value);
    else setError(result.refusal.messages[0] ?? t("failed"));
  }

  async function issue() {
    const result = await kioskCall<BadgeIssued>(`/api/v1/identity/badges/${person}`, "POST");
    if (!result.ok) {
      setError(result.refusal.messages[0] ?? t("failed"));
      return;
    }
    setIssued(result.value);
    setBadge((b) => (b ? { ...b, issued: true } : b));
  }

  async function resetPin() {
    const result = await kioskCall<KioskBadge>(`/api/v1/identity/badges/${person}:reset-pin`, "POST");
    if (result.ok) setBadge(result.value);
    else setError(result.refusal.messages[0] ?? t("failed"));
  }

  return (
    <section className="kiosk-panel" aria-labelledby="kiosk-badges-title">
      <h2 id="kiosk-badges-title">{t("badgesTitle")}</h2>
      <p className="field-hint">{t("badgesIntro")}</p>
      <div className="kiosk-print-hide">
        <PersonPicker label={t("person")} value={person} onChange={(id) => void pick(id)} />
      </div>
      {error ? (
        <Alert tone="danger" title={t("failed")} live>
          {error}
        </Alert>
      ) : null}
      {badge ? (
        <div className="kiosk-form">
          <p>{badge.issued ? (badge.pinSet ? t("badgeReady") : t("badgeNoPin")) : t("badgeNone")}</p>
          {badge.lockedUntil ? <Badge tone="warning">{t("badgeLocked")}</Badge> : null}
          <div className="kiosk-actions kiosk-print-hide">
            {badge.issued && (badge.pinSet || badge.lockedUntil) ? (
              <ConfirmAction label={t("resetPin")} question={t("resetPinQuestion")} confirmLabel={t("resetPinConfirm")} cancelLabel={t("cancel")} onConfirm={resetPin} />
            ) : null}
            {badge.issued ? (
              <ConfirmAction label={t("reissue")} question={t("reissueQuestion")} confirmLabel={t("reissueConfirm")} cancelLabel={t("cancel")} onConfirm={issue} />
            ) : (
              <Button onClick={() => void issue()}>{t("issue")}</Button>
            )}
          </div>
        </div>
      ) : null}
      {issued ? (
        <figure className="kiosk-panel">
          {/* The QR is an inline SVG made by the API for printing once, not a file next/image could optimize. */}
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img className="kiosk-qr" src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(issued.qrSvg)}`} alt={t("qrAlt")} />
          <figcaption>
            <p className="kiosk-code">{issued.code}</p>
            <p className="field-hint">{t("printHint")}</p>
          </figcaption>
          <div className="kiosk-actions kiosk-print-hide">
            <Button variant="secondary" onClick={() => window.print()}>
              {t("print")}
            </Button>
          </div>
        </figure>
      ) : null}
    </section>
  );
}
