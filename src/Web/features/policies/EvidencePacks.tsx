"use client";

import { useEffect, useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { evidenceApi, nameIn, type EvidencePackStatus, type EvidencePackView, type Names } from "@/lib/policies/types";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField } from "@/components/ui/FormField";
import "./policies.css";

type Choice = { id: string; code: string; names: Names };

type OwnedSpace = { id: string; name: string; role: string | null; status: string };

const tones: Record<EvidencePackStatus, "neutral" | "success" | "danger"> = { queued: "neutral", ready: "success", failed: "danger" };

/**
 * Evidence packs for auditors (spec 025 US8 scenarios 1 and 2): a compliance officer picks policies and a period and
 * asks for a pack, which the background builds as PDF and CSV; a ready pack downloads or is shared read-only with a
 * space they own until a date. The API checks every policy, the period, the space and the date, and audits each step.
 */
export function EvidencePacks({ policies, packs, spaceView = false }: { policies: Choice[]; packs: EvidencePackView[]; spaceView?: boolean }) {
  const t = useTranslations("policies.evidence");
  const locale = useLocale();
  const format = useFormatter();
  const router = useRouter();
  const ids = useId();
  const [chosen, setChosen] = useState<string[]>([]);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [spaces, setSpaces] = useState<OwnedSpace[]>([]);
  const [shareTo, setShareTo] = useState<Record<string, string>>({});
  const [until, setUntil] = useState<Record<string, string>>({});

  useEffect(() => {
    if (spaceView) return;
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/spaces?scope=mine", { cache: "no-store" });
        if (live && response.ok) setSpaces(((await response.json()) as OwnedSpace[]).filter((s) => s.role === "owner" && s.status === "active"));
      } catch {
        // Without the list a pack can still be asked for and downloaded.
      }
    })();
    return () => {
      live = false;
    };
  }, [spaceView]);

  const day = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium" });

  async function send(key: string, url: string, method: "POST" | "PUT", body: unknown) {
    setBusy(key);
    setMessages([]);
    try {
      const response = await fetch(url, { method, headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return false;
      }
      router.refresh();
      return true;
    } catch {
      setMessages([t("failed")]);
      return false;
    } finally {
      setBusy(null);
    }
  }

  async function request(e: React.FormEvent) {
    e.preventDefault();
    if (chosen.length === 0) {
      setMessages([t("choosePolicies")]);
      return;
    }
    if (await send("request", evidenceApi, "POST", { policyIds: chosen, from: from || null, to: to || null })) {
      setChosen([]);
    }
  }

  return (
    <>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {spaceView ? null : (
        <form className="policies-card" aria-labelledby={`${ids}-ask`} onSubmit={(e) => void request(e)} noValidate data-testid="evidence-form">
          <h2 id={`${ids}-ask`}>{t("askTitle")}</h2>
          <p className="policies-meta">{t("askIntro")}</p>
          <fieldset className="policies-choices">
            <legend>{t("policies")}</legend>
            {policies.length === 0 ? <p className="policies-meta">{t("noPolicies")}</p> : null}
            {policies.map((p) => (
              <Checkbox
                key={p.id}
                label={<bdi>{t("policyName", { code: p.code, name: nameIn(p.names, locale) })}</bdi>}
                checked={chosen.includes(p.id)}
                onChange={(e) => setChosen((c) => (e.target.checked ? [...c, p.id] : c.filter((x) => x !== p.id)))}
              />
            ))}
          </fieldset>
          <div className="ui-control-row">
            <FormField label={t("from")} name="evidenceFrom" type="date" value={from} required onChange={(e) => setFrom(e.target.value)} />
            <FormField label={t("to")} name="evidenceTo" type="date" value={to} required onChange={(e) => setTo(e.target.value)} hint={t("rangeHint")} />
          </div>
          <div className="button-row">
            <Button type="submit" variant="primary" busy={busy === "request"} busyLabel={t("busy")}>
              {t("ask")}
            </Button>
          </div>
        </form>
      )}
      <section className="policies-card" aria-labelledby={`${ids}-list`}>
        <h2 id={`${ids}-list`}>{t(spaceView ? "sharedTitle" : "listTitle")}</h2>
        {packs.length === 0 ? (
          <p className="policies-meta">{t(spaceView ? "noShared" : "noPacks")}</p>
        ) : (
          <div className="policies-table">
            <table data-testid="evidence-packs" data-stack>
              <thead>
                <tr>
                  <th scope="col">{t("period")}</th>
                  <th scope="col">{t("policies")}</th>
                  <th scope="col">{t("status")}</th>
                  <th scope="col">{t("files")}</th>
                </tr>
              </thead>
              <tbody>
                {packs.map((p) => (
                  <tr key={p.id} data-testid="evidence-pack">
                    <th scope="row" data-label={t("period")}>
                      <bdi>{t("range", { from: day(p.from), to: day(p.to) })}</bdi>
                      <span className="policies-meta"> {t("askedBy", { name: p.requestedBy.displayName, date: day(p.requestedAt) })}</span>
                    </th>
                    <td data-label={t("policies")}>
                      {p.policies.map((x) => (
                        <bdi key={x.id} className="policies-line">
                          {t("policyName", { code: x.code, name: nameIn(x.names, locale) })}
                        </bdi>
                      ))}
                    </td>
                    <td data-label={t("status")}>
                      <Badge tone={tones[p.status]}>{t(`statuses.${p.status}`)}</Badge>
                      {p.sharedSpace && p.sharedUntil ? (
                        <span className="policies-meta"> {t("sharedWith", { space: p.sharedSpace.name, date: day(p.sharedUntil) })}</span>
                      ) : null}
                    </td>
                    <td data-label={t("files")}>
                      {p.status === "ready" ? (
                        <div className="button-row">
                          {p.pdf ? (
                            <ButtonLink size="sm" icon="download" href={`${evidenceApi}/${encodeURIComponent(p.id)}/pdf`} download>
                              {t("pdf")}
                            </ButtonLink>
                          ) : (
                            <span className="policies-meta">{t("noPdf")}</span>
                          )}
                          {p.csv ? (
                            <ButtonLink size="sm" icon="download" href={`${evidenceApi}/${encodeURIComponent(p.id)}/csv`} download>
                              {t("csv")}
                            </ButtonLink>
                          ) : null}
                        </div>
                      ) : (
                        <span className="policies-meta">{t("building")}</span>
                      )}
                      {p.canShare ? (
                        <form
                          className="ui-control-row"
                          aria-label={t("shareLabel")}
                          onSubmit={(e) => {
                            e.preventDefault();
                            const space = shareTo[p.id] ?? p.sharedSpace?.id ?? "";
                            void send(`share-${p.id}`, `${evidenceApi}/${encodeURIComponent(p.id)}/share`, "PUT", {
                              spaceId: space || null,
                              until: space ? until[p.id] || null : null,
                            });
                          }}
                        >
                          <SelectField
                            label={t("space")}
                            name={`evidenceSpace-${p.id}`}
                            value={shareTo[p.id] ?? p.sharedSpace?.id ?? ""}
                            onChange={(e) => setShareTo((s) => ({ ...s, [p.id]: e.target.value }))}
                            options={[
                              { value: "", label: t("notShared") },
                              ...spaces.map((s) => ({ value: s.id, label: s.name })),
                              ...(p.sharedSpace && !spaces.some((s) => s.id === p.sharedSpace?.id) ? [{ value: p.sharedSpace.id, label: p.sharedSpace.name }] : []),
                            ]}
                          />
                          <FormField
                            label={t("until")}
                            name={`evidenceUntil-${p.id}`}
                            type="date"
                            value={until[p.id] ?? ""}
                            onChange={(e) => setUntil((u) => ({ ...u, [p.id]: e.target.value }))}
                          />
                          <Button type="submit" busy={busy === `share-${p.id}`} busyLabel={t("busy")}>
                            {t("share")}
                          </Button>
                        </form>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </>
  );
}
