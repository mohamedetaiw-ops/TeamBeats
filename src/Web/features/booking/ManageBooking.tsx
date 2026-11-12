"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { PersonPicker } from "@/features/services/PersonPicker";
import {
  bookingApi,
  call,
  kioskHref,
  manageSiteHref,
  pick,
  qrImageHref,
  send,
  siteLabelsHref,
  type FloorView,
  type ResourceKind,
  type ResourceView,
  type RuleView,
  type SiteDetail,
  type SiteView,
} from "@/lib/booking";
import "./booking.css";

/** Names typed for the viewer's language; the API keeps one name per language. */
const named = (locale: string, value: string) => ({ [locale]: value.trim() });

/** The sites a facilities administrator manages (spec 031 US1, FR-001), and a new one. The API decides the scope. */
export function ManageSites({ initial }: { initial: SiteView[] }) {
  const t = useTranslations("booking.manage");
  const locale = useLocale();
  const router = useRouter();
  const [name, setName] = useState("");
  const [timeZone, setTimeZone] = useState("");
  const [opensAt, setOpensAt] = useState("08:00");
  const [closesAt, setClosesAt] = useState("18:00");
  const [messages, setMessages] = useState<string[]>([]);

  async function create() {
    const body = { names: named(locale, name), timeZone: timeZone.trim() || null, opensAt, closesAt };
    const result = await call<SiteView>(() => send(`${bookingApi}/manage/sites`, "POST", body), t("failed"));
    if (!result.ok) return setMessages(result.messages);
    router.push(manageSiteHref(result.value.id));
  }

  return (
    <div className="booking">
      {initial.length === 0 ? <StateView kind="empty" title={t("noSitesTitle")} message={t("noSites")} icon="building" /> : null}
      <ul className="booking-list" aria-label={t("sitesLabel")}>
        {initial.map((s) => (
          <li key={s.id} className="booking-card">
            <h3>
              <Link href={manageSiteHref(s.id)}>
                <bdi>{pick(s.names, locale)}</bdi>
              </Link>
            </h3>
            <p className="booking-meta">{t("hours", { opens: s.opensAt.slice(0, 5), closes: s.closesAt.slice(0, 5), zone: s.timeZone })}</p>
            {s.status === "removed" ? <Badge>{t("removed")}</Badge> : null}
          </li>
        ))}
      </ul>
      <form
        className="booking-section booking-form"
        aria-labelledby="booking-new-site"
        onSubmit={(e) => {
          e.preventDefault();
          void create();
        }}
      >
        <h2 id="booking-new-site">{t("newSite")}</h2>
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        <FormField label={t("siteName")} name="siteName" maxLength={120} value={name} onChange={(e) => setName(e.target.value)} required />
        <div className="booking-row">
          <FormField label={t("timeZone")} name="timeZone" hint={t("timeZoneHint")} value={timeZone} onChange={(e) => setTimeZone(e.target.value)} />
          <FormField label={t("opensAt")} name="opensAt" type="time" value={opensAt} onChange={(e) => setOpensAt(e.target.value)} required />
          <FormField label={t("closesAt")} name="closesAt" type="time" value={closesAt} onChange={(e) => setClosesAt(e.target.value)} required />
        </div>
        <div className="booking-row">
          <Button type="submit" variant="primary" icon="plus">
            {t("createSite")}
          </Button>
        </div>
      </form>
    </div>
  );
}

const kinds: ResourceKind[] = ["room", "desk", "parking", "equipment"];

/**
 * One site for its facilities administrators (spec 031 US1): floors and zones, resources of each kind in the plan,
 * the rule of a resource, removal (holders of future bookings are told), and the QR labels to print.
 */
export function SiteAdmin({ initial, inPlan }: { initial: SiteDetail; inPlan: ResourceKind[] }) {
  const t = useTranslations("booking.manage");
  const tk = useTranslations("booking.kinds");
  const locale = useLocale();
  const router = useRouter();
  const [detail, setDetail] = useState(initial);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [floorName, setFloorName] = useState("");
  const [zoneName, setZoneName] = useState("");
  const [zoneFloor, setZoneFloor] = useState(initial.floors[0]?.id ?? "");
  const [kind, setKind] = useState<ResourceKind>(inPlan[0] ?? "desk");
  const [resourceName, setResourceName] = useState("");
  const [capacity, setCapacity] = useState("");
  const [attributes, setAttributes] = useState("");
  const [placement, setPlacement] = useState("");
  const [mailbox, setMailbox] = useState("");
  const [checkIn, setCheckIn] = useState(true);
  const [needsApproval, setNeedsApproval] = useState(false);
  const [approver, setApprover] = useState<string | null>(null);
  const [ruleFor, setRuleFor] = useState<string | null>(null);
  const [rule, setRule] = useState<RuleView | null>(null);
  const siteId = detail.site.id;
  const floorOf = (id: string | null): FloorView | undefined => detail.floors.find((f) => f.id === id);

  async function reload(done?: string) {
    const fresh = await call<SiteDetail>(() => fetch(`${bookingApi}/manage/sites/${siteId}`), t("failed"));
    if (fresh.ok) setDetail(fresh.value);
    setNotice(done ?? null);
    router.refresh();
  }

  async function apply<T>(run: () => Promise<Response>, done: string): Promise<T | null> {
    setNotice(null);
    const result = await call<T>(run, t("failed"));
    if (!result.ok) {
      setMessages(result.messages);
      return null;
    }
    setMessages([]);
    await reload(done);
    return result.value;
  }

  async function addResource() {
    const [floorId, zoneId] = placement ? placement.split("/") : [null, null];
    const body = {
      siteId,
      floorId: floorId || null,
      zoneId: zoneId || null,
      kind,
      names: named(locale, resourceName),
      capacity: capacity ? Number(capacity) : null,
      attributes: attributes.split(",").map((a) => a.trim()).filter(Boolean),
      mailbox: kind === "room" && mailbox.trim() ? mailbox.trim() : null,
      checkIn,
      needsApproval,
      approverIds: needsApproval && approver ? [approver] : [],
    };
    if (await apply<ResourceView>(() => send(`${bookingApi}/manage/resources`, "POST", body), t("resourceAdded", { name: resourceName.trim() }))) {
      setResourceName("");
      setCapacity("");
      setAttributes("");
      setMailbox("");
      setNeedsApproval(false);
      setApprover(null);
    }
  }

  async function openRule(resource: ResourceView) {
    setRuleFor(resource.id);
    const found = await call<RuleView>(() => fetch(`${bookingApi}/manage/resources/${resource.id}/rule`), t("failed"));
    if (found.ok) setRule(found.value);
    else setMessages(found.messages);
  }

  async function saveRule() {
    if (!ruleFor || !rule) return;
    const body = {
      advanceDays: rule.advanceDays,
      minMinutes: rule.minMinutes,
      maxMinutes: rule.maxMinutes,
      workingHours: rule.workingHours,
      bufferMinutes: rule.bufferMinutes,
      maxActive: rule.maxActive,
      overnight: rule.overnight,
    };
    const saved = await apply<RuleView>(() => send(`${bookingApi}/manage/resources/${ruleFor}/rule`, "PUT", body), t("ruleSaved"));
    if (saved) setRule(saved);
  }

  const placements = [
    { value: "", label: t("noFloor") },
    ...detail.floors.flatMap((f) => [
      { value: `${f.id}/`, label: pick(f.names, locale) },
      ...f.zones.map((z) => ({ value: `${f.id}/${z.id}`, label: `${pick(f.names, locale)} › ${pick(z.names, locale)}` })),
    ]),
  ];
  const number = (key: keyof RuleView) => (e: { target: { value: string } }) => setRule((r) => (r ? { ...r, [key]: Number(e.target.value) } : r));

  return (
    <div className="booking">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {notice ? (
        <p role="status" className="booking-meta">
          {notice}
        </p>
      ) : null}

      <section className="booking-section" aria-labelledby="booking-places">
        <h2 id="booking-places">{t("placesTitle")}</h2>
        <ul className="booking-plain" aria-label={t("floorsLabel")}>
          {detail.floors.map((f) => (
            <li key={f.id}>
              <bdi>{pick(f.names, locale)}</bdi>
              {f.zones.length > 0 ? <span className="booking-meta"> {f.zones.map((z) => pick(z.names, locale)).join(", ")}</span> : null}
            </li>
          ))}
        </ul>
        <div className="booking-row">
          <FormField label={t("floorName")} name="floorName" maxLength={120} value={floorName} onChange={(e) => setFloorName(e.target.value)} />
          <Button size="sm" icon="plus" disabled={!floorName.trim()}
            onClick={() => void apply(() => send(`${bookingApi}/manage/sites/${siteId}/floors`, "POST", { names: named(locale, floorName), sort: detail.floors.length + 1 }),
              t("floorAdded")).then(() => setFloorName(""))}>
            {t("addFloor")}
          </Button>
        </div>
        {detail.floors.length > 0 ? (
          <div className="booking-row">
            <SelectField label={t("zoneFloor")} name="zoneFloor" value={zoneFloor} onChange={(e) => setZoneFloor(e.target.value)}
              options={detail.floors.map((f) => ({ value: f.id, label: pick(f.names, locale) }))} />
            <FormField label={t("zoneName")} name="zoneName" maxLength={120} value={zoneName} onChange={(e) => setZoneName(e.target.value)} />
            <Button size="sm" icon="plus" disabled={!zoneName.trim() || !zoneFloor}
              onClick={() => void apply(() => send(`${bookingApi}/manage/sites/${siteId}/floors/${zoneFloor}/zones`, "POST", { names: named(locale, zoneName) }),
                t("zoneAdded")).then(() => setZoneName(""))}>
              {t("addZone")}
            </Button>
          </div>
        ) : null}
      </section>

      <section className="booking-section" aria-labelledby="booking-resources">
        <h2 id="booking-resources">{t("resourcesTitle", { count: detail.resources.length })}</h2>
        <div className="booking-row">
          <ButtonLink size="sm" icon="download" href={siteLabelsHref(siteId)}>
            {t("labels")}
          </ButtonLink>
          <ButtonLink size="sm" variant="quiet" icon="check" href={kioskHref(siteId)}>
            {t("kiosk")}
          </ButtonLink>
        </div>
        <ul className="booking-list" aria-label={t("resourcesLabel")}>
          {detail.resources.map((r) => (
            <li key={r.id} className="booking-card">
              <h3>
                <bdi>{pick(r.names, locale)}</bdi>
              </h3>
              <p className="booking-meta">
                {tk(r.kind)}
                {floorOf(r.floorId) ? ` · ${pick(floorOf(r.floorId)!.names, locale)}` : ""}
                {r.capacity ? ` · ${t("seats", { count: r.capacity })}` : ""}
              </p>
              <div className="booking-row">
                {r.attributes.map((a) => (
                  <Badge key={a}>{a}</Badge>
                ))}
                {r.fixedUserId ? <Badge tone="info">{t("fixed")}</Badge> : null}
                {!r.checkIn ? <Badge>{t("noCheckIn")}</Badge> : null}
                {r.needsApproval ? <Badge tone="warning">{t("needsApproval")}</Badge> : null}
              </div>
              <div className="booking-row">
                <Button size="sm" variant="quiet" icon="settings" onClick={() => void openRule(r)}>
                  {t("rule")}
                </Button>
                <ConfirmAction label={t("remove")} question={t("removeQuestion", { name: pick(r.names, locale) })} confirmLabel={t("removeConfirm")} cancelLabel={t("keep")}
                  onConfirm={() => void apply(() => send(`${bookingApi}/manage/resources/${r.id}`, "DELETE"), t("removedNotice", { name: pick(r.names, locale) }))} />
              </div>
              {ruleFor === r.id && rule ? (
                <form
                  className="booking-form"
                  aria-label={t("ruleLabel", { name: pick(r.names, locale) })}
                  onSubmit={(e) => {
                    e.preventDefault();
                    void saveRule();
                  }}
                >
                  <p className="booking-meta">{t(`ruleSource.${rule.source}`)}</p>
                  <div className="booking-row">
                    <FormField label={t("advanceDays")} name="advanceDays" type="number" min={0} max={365} value={rule.advanceDays} onChange={number("advanceDays")} />
                    <FormField label={t("minMinutes")} name="minMinutes" type="number" min={5} max={1440} value={rule.minMinutes} onChange={number("minMinutes")} />
                    <FormField label={t("maxMinutes")} name="maxMinutes" type="number" min={5} max={20160} value={rule.maxMinutes} onChange={number("maxMinutes")} />
                    <FormField label={t("bufferMinutes")} name="bufferMinutes" type="number" min={0} max={240} value={rule.bufferMinutes} onChange={number("bufferMinutes")} />
                    <FormField label={t("maxActive")} name="maxActive" type="number" min={1} max={100} value={rule.maxActive} onChange={number("maxActive")} />
                  </div>
                  <Checkbox label={t("workingHours")} checked={rule.workingHours} onChange={(e) => setRule({ ...rule, workingHours: e.target.checked })} />
                  <Checkbox label={t("overnight")} checked={rule.overnight} onChange={(e) => setRule({ ...rule, overnight: e.target.checked })} />
                  <div className="booking-row">
                    <Button type="submit" size="sm" icon="check">
                      {t("saveRule")}
                    </Button>
                  </div>
                </form>
              ) : null}
            </li>
          ))}
        </ul>
      </section>

      {inPlan.length > 0 ? (
        <form
          className="booking-section booking-form"
          aria-labelledby="booking-new-resource"
          onSubmit={(e) => {
            e.preventDefault();
            void addResource();
          }}
        >
          <h2 id="booking-new-resource">{t("newResource")}</h2>
          <div className="booking-row">
            <SelectField label={t("kind")} name="kind" value={kind} onChange={(e) => setKind(e.target.value as ResourceKind)}
              options={kinds.filter((k) => inPlan.includes(k)).map((k) => ({ value: k, label: tk(k) }))} />
            <FormField label={t("resourceName")} name="resourceName" maxLength={120} value={resourceName} onChange={(e) => setResourceName(e.target.value)} required />
            <SelectField label={t("placement")} name="placement" value={placement} onChange={(e) => setPlacement(e.target.value)} options={placements} />
          </div>
          <div className="booking-row">
            <FormField label={t("capacity")} name="capacity" type="number" min={1} max={1000} value={capacity} onChange={(e) => setCapacity(e.target.value)}
              required={kind === "room"} />
            <FormField label={t("attributes")} name="attributes" hint={t("attributesHint")} value={attributes} onChange={(e) => setAttributes(e.target.value)} />
            {kind === "room" ? (
              <FormField label={t("mailbox")} name="mailbox" type="email" hint={t("mailboxHint")} value={mailbox} onChange={(e) => setMailbox(e.target.value)} />
            ) : null}
          </div>
          <Checkbox label={t("checkIn")} hint={t("checkInHint")} checked={checkIn} onChange={(e) => setCheckIn(e.target.checked)} />
          <Checkbox label={t("approval")} hint={t("approvalHint")} checked={needsApproval} onChange={(e) => setNeedsApproval(e.target.checked)} />
          {needsApproval ? <PersonPicker label={t("approver")} value={approver} onChange={setApprover} /> : null}
          <div className="booking-row">
            <Button type="submit" variant="primary" icon="plus">
              {t("addResource")}
            </Button>
          </div>
        </form>
      ) : null}
    </div>
  );
}

/** The QR labels of a site to print (US1 scenario 1): each resource with its code and the address it opens. */
export function Labels({ labels, origin }: { labels: { resourceId: string; kind: ResourceKind; names: Record<string, string>; floor: Record<string, string> | null; code: string; link: string }[]; origin: string }) {
  const t = useTranslations("booking.manage");
  const tk = useTranslations("booking.kinds");
  const locale = useLocale();
  if (labels.length === 0) return <StateView kind="empty" title={t("noLabelsTitle")} message={t("noLabels")} icon="building" />;
  return (
    <div className="booking">
      <div className="booking-row booking-noprint">
        <Button icon="download" onClick={() => window.print()}>
          {t("print")}
        </Button>
      </div>
      <ul className="booking-labels" aria-label={t("labels")}>
        {labels.map((l) => (
          <li key={l.resourceId} className="booking-label">
            {/* eslint-disable-next-line @next/next/no-img-element -- the API draws the code as SVG; next/image adds nothing for it. */}
            <img src={qrImageHref(l.resourceId)} alt={t("qrAlt", { name: pick(l.names, locale) })} width={160} height={160} className="booking-qr" />
            <strong>
              <bdi>{pick(l.names, locale)}</bdi>
            </strong>
            <span className="booking-meta">
              {tk(l.kind)}
              {l.floor ? ` · ${pick(l.floor, locale)}` : ""}
            </span>
            <code dir="ltr">{l.code}</code>
            <span className="booking-meta" dir="ltr">
              {origin}
              {l.link}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
