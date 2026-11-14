"use client";

import Link from "next/link";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, SelectField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { pickName, siteTemplates, type ContentAudience, type SiteView } from "@/lib/content/types";
import type { OrgUnit } from "@/lib/experience/builder-types";
import "./content.css";

const sortedUnits = (units: OrgUnit[], locale: string) =>
  [...units].filter((u) => u.status === "active").sort((a, b) => a.level - b.level || pickName(a.names, locale).localeCompare(pickName(b.names, locale), locale));

function Problems({ messages }: { messages: string[] }) {
  return messages.length > 0 ? (
    <div className="notice notice-error" role="alert">
      {messages.map((m) => (
        <p key={m}>{m}</p>
      ))}
    </div>
  ) : null;
}

/**
 * Creating a site (spec 009 US6 scenario 1): names, the part of the organization it serves, a template and the path of
 * its home page. The API decides who may create sites and makes the home page in the page builder.
 */
export function SiteCreateForm({ units }: { units: OrgUnit[] }) {
  const t = useTranslations("content.sites");
  const locale = useLocale();
  const router = useRouter();
  const [nameEn, setNameEn] = useState("");
  const [nameAr, setNameAr] = useState("");
  const [unit, setUnit] = useState("");
  const [template, setTemplate] = useState<string>(siteTemplates[0]);
  const [slug, setSlug] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function create(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setMessages([]);
    const names: Record<string, string> = {};
    if (nameEn.trim()) names.en = nameEn.trim();
    if (nameAr.trim()) names.ar = nameAr.trim();
    try {
      const response = await fetch("/api/v1/content/sites", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ names, orgUnitId: unit || null, template, homeSlug: slug.trim() || null }),
      });
      if (response.ok) {
        const site = (await response.json()) as SiteView;
        router.push(`/content/sites/${encodeURIComponent(site.id)}`);
        return;
      }
      setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="content-card" aria-labelledby="site-create">
      <h2 id="site-create">{t("create")}</h2>
      <p className="field-hint">{t("createIntro")}</p>
      <Problems messages={messages} />
      <form className="content-site-form" onSubmit={(e) => void create(e)}>
        <FormField label={t("nameEn")} name="nameEn" value={nameEn} onChange={(e) => setNameEn(e.target.value)} dir="ltr" />
        <FormField label={t("nameAr")} name="nameAr" value={nameAr} onChange={(e) => setNameAr(e.target.value)} dir="rtl" lang="ar" />
        <SelectField
          label={t("unit")}
          hint={t("unitHint")}
          name="orgUnitId"
          value={unit}
          onChange={(e) => setUnit(e.target.value)}
          options={[{ value: "", label: t("wholeTenant") }, ...sortedUnits(units, locale).map((u) => ({ value: u.id, label: pickName(u.names, locale) }))]}
        />
        <SelectField
          label={t("template")}
          hint={t(`templateHints.${template}` as "templateHints.subsidiary")}
          name="template"
          value={template}
          onChange={(e) => setTemplate(e.target.value)}
          options={siteTemplates.map((k) => ({ value: k, label: t(`templates.${k}`) }))}
        />
        <FormField label={t("homeSlug")} hint={t("homeSlugHint")} name="homeSlug" value={slug} onChange={(e) => setSlug(e.target.value)} dir="ltr" />
        <p className="content-actions">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("creating")}>
            {t("create")}
          </Button>
        </p>
      </form>
    </section>
  );
}

const sharedUnits = (audience: ContentAudience | null) => new Set((audience?.anyOf ?? []).flatMap((c) => (c.orgUnits ?? []).map((u) => u.id)));

/**
 * A site's own settings for its owner (spec 009 US6 scenarios 2 to 5): names, comments, moderation, the units it is
 * shared with, the home page link, and archive or restore. Theme and navigation live with the site's unit in branding
 * and the page builder; this page links there.
 */
export function SiteSettings({ site, units, locales }: { site: SiteView; units: OrgUnit[]; locales: string[] }) {
  const t = useTranslations("content.sites");
  const locale = useLocale();
  const router = useRouter();
  const [names, setNames] = useState<Record<string, string>>(site.names);
  const [comments, setComments] = useState(site.commentsMode === "on");
  const [moderation, setModeration] = useState(site.moderation);
  const [shared, setShared] = useState<Set<string>>(sharedUnits(site.sharedAudience));
  const [home, setHome] = useState(site.homePageId ?? "");
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const archived = site.status === "archived";
  const name = pickName(site.names, locale);
  const homePath = site.homeSlug ? `/p/${site.homeSlug}` : null;

  async function send(url: string, body?: unknown) {
    setBusy(true);
    setSaved(false);
    setMessages([]);
    try {
      const response = await fetch(url, {
        method: body === undefined ? "POST" : "PUT",
        headers: body === undefined ? undefined : { "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (response.ok) {
        setSaved(body !== undefined);
        router.refresh();
        return;
      }
      setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  function save(event: React.FormEvent) {
    event.preventDefault();
    const audience = shared.size === 0 ? null : { anyOf: [{ orgUnits: [...shared].map((id) => ({ id, subtree: true })) }] };
    void send(`/api/v1/content/sites/${encodeURIComponent(site.id)}`, {
      version: site.version,
      names: Object.fromEntries(Object.entries(names).filter(([, v]) => v.trim())),
      commentsMode: comments ? "on" : "off",
      moderation,
      sharedAudience: audience,
      unshare: audience === null,
      homePageId: home.trim() && home.trim() !== site.homePageId ? home.trim() : null,
    });
  }

  return (
    <>
      {archived ? (
        <div className="notice" role="status">
          <p>{t("archivedNote")}</p>
        </div>
      ) : null}
      <Problems messages={messages} />
      {saved ? (
        <p className="notice notice-success" role="status">
          {t("saved")}
        </p>
      ) : null}
      <section className="content-card" aria-labelledby="site-home">
        <h2 id="site-home">{t("home")}</h2>
        {site.homePageId ? (
          <p>
            <Link href={`/admin/pages/${encodeURIComponent(site.homePageId)}`}>{t("editHome")}</Link>
            {site.homeSlug ? (
              <>
                {" "}
                <bdi dir="ltr">{homePath}</bdi>
              </>
            ) : null}
          </p>
        ) : (
          <p className="field-hint">{t("noHome")}</p>
        )}
        <h2>{t("theme")}</h2>
        <p className="field-hint">{t("themeHint")}</p>
        <p className="content-actions">
          <Link href="/admin/branding">{t("openBranding")}</Link>
          <Link href={`/content/items?site=${encodeURIComponent(site.id)}`}>{t("items")}</Link>
        </p>
      </section>
      <section className="content-card" aria-labelledby="site-settings">
        <h2 id="site-settings">{t("settings")}</h2>
        <form className="content-site-form" onSubmit={save}>
          {locales.map((l) => (
            <FormField
              key={l}
              label={l === "ar" ? t("nameAr") : t("nameEn")}
              name={`name-${l}`}
              value={names[l] ?? ""}
              onChange={(e) => setNames({ ...names, [l]: e.target.value })}
              dir={l === "ar" ? "rtl" : "ltr"}
              lang={l}
              disabled={archived}
            />
          ))}
          <Checkbox label={t("comments")} checked={comments} onChange={(e) => setComments(e.target.checked)} disabled={archived} />
          <Checkbox label={t("moderation")} checked={moderation} onChange={(e) => setModeration(e.target.checked)} disabled={archived} />
          {site.isDefault ? null : (
            <fieldset className="content-site-shared">
              <legend>{t("shared")}</legend>
              <p className="field-hint">{t("sharedHint")}</p>
              <ul className="content-list content-units">
                {sortedUnits(units, locale)
                  .filter((u) => u.id !== site.orgUnitId)
                  .map((u) => (
                    <li key={u.id}>
                      <Checkbox
                        label={pickName(u.names, locale)}
                        checked={shared.has(u.id)}
                        disabled={archived}
                        onChange={(e) => {
                          const next = new Set(shared);
                          if (e.target.checked) next.add(u.id);
                          else next.delete(u.id);
                          setShared(next);
                        }}
                      />
                    </li>
                  ))}
              </ul>
            </fieldset>
          )}
          {site.homePageId ? null : <FormField label={t("homePageId")} name="homePageId" value={home} onChange={(e) => setHome(e.target.value)} dir="ltr" disabled={archived} />}
          {archived ? null : (
            <p className="content-actions">
              <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
                {t("save")}
              </Button>
            </p>
          )}
        </form>
      </section>
      {site.isDefault ? null : (
        <section className="content-card" aria-label={archived ? t("restore") : t("archive")}>
          {archived ? (
            <Button busy={busy} busyLabel={t("saving")} onClick={() => void send(`/api/v1/content/sites/${encodeURIComponent(site.id)}/restore`)}>
              {t("restore")}
            </Button>
          ) : (
            <ConfirmAction
              label={t("archive")}
              question={t("archiveQuestion", { name })}
              confirmLabel={t("archiveConfirm")}
              cancelLabel={t("cancel")}
              onConfirm={() => send(`/api/v1/content/sites/${encodeURIComponent(site.id)}/archive`)}
            />
          )}
        </section>
      )}
    </>
  );
}
