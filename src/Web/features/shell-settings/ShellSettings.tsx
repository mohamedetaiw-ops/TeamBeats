"use client";

import { useState, type FormEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import "./shell-settings.css";

type Labels = Record<string, string>;
type LinkKind = "privacy" | "acceptable_use" | "accessibility" | "other";
type FooterConfig = { organization: Labels | null; links: { kind: LinkKind; labels: Labels; url: string }[] | null; classification: Labels | null };
type ViewConfig = { id: string; labels: Labels; pageId: string; audienceRuleId: string | null };

export type ShellAdmin = {
  version: number;
  definition: Record<string, unknown> & { homePageId?: string | null; footer?: FooterConfig | null; homeViews?: ViewConfig[] | null };
};

export type AudienceOption = { id: string; labels: Labels };

type LinkRow = { key: string; kind: LinkKind; en: string; ar: string; url: string };
type ViewRow = { key: string; id: string; en: string; ar: string; pageId: string; audienceRuleId: string };

const kinds: LinkKind[] = ["privacy", "acceptable_use", "accessibility", "other"];
const newKey = () => crypto.randomUUID();

/** Labels with only the languages that were filled in; null when none were. */
function labels(en: string, ar: string): Labels | null {
  const result: Labels = {};
  if (en.trim()) result.en = en.trim();
  if (ar.trim()) result.ar = ar.trim();
  return Object.keys(result).length > 0 ? result : null;
}

/**
 * The shell settings form (spec 008 FR-056, FR-058). It edits only the footer and the home views and sends the rest of
 * the published shell back as it was. What is valid, who may save and which audiences exist are the API's answers.
 */
export function ShellSettings({ initial, pages, audiences }: { initial: ShellAdmin; pages: { id: string; slug: string }[]; audiences: AudienceOption[] | null }) {
  const t = useTranslations("shell_settings");
  const locale = useLocale();
  const router = useRouter();
  const definition = initial.definition;
  const footer = definition.footer ?? null;
  const [orgEn, setOrgEn] = useState(footer?.organization?.en ?? "");
  const [orgAr, setOrgAr] = useState(footer?.organization?.ar ?? "");
  const [classEn, setClassEn] = useState(footer?.classification?.en ?? "");
  const [classAr, setClassAr] = useState(footer?.classification?.ar ?? "");
  const [links, setLinks] = useState<LinkRow[]>(
    (footer?.links ?? []).map((l) => ({ key: newKey(), kind: l.kind, en: l.labels.en ?? "", ar: l.labels.ar ?? "", url: l.url })),
  );
  const [views, setViews] = useState<ViewRow[]>(
    (definition.homeViews ?? []).map((v) => ({ key: newKey(), id: v.id, en: v.labels.en ?? "", ar: v.labels.ar ?? "", pageId: v.pageId, audienceRuleId: v.audienceRuleId ?? "" })),
  );
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);
  const [saving, setSaving] = useState(false);

  const home = definition.homePageId ?? null;
  const pageOptions = [{ value: "", label: t("views.choosePage") }, ...pages.filter((p) => p.id !== home).map((p) => ({ value: p.id, label: p.slug }))];
  const audienceOptions = [
    { value: "", label: t("views.everyone") },
    ...(audiences ?? []).map((a) => ({ value: a.id, label: a.labels[locale] ?? a.labels.en ?? Object.values(a.labels)[0] ?? a.id })),
  ];
  const updateLink = (key: string, change: Partial<LinkRow>) => setLinks((rows) => rows.map((r) => (r.key === key ? { ...r, ...change } : r)));
  const updateView = (key: string, change: Partial<ViewRow>) => setViews((rows) => rows.map((r) => (r.key === key ? { ...r, ...change } : r)));

  async function save(event: FormEvent) {
    event.preventDefault();
    setSaving(true);
    setResult(null);
    const organization = labels(orgEn, orgAr);
    const classification = labels(classEn, classAr);
    const footerLinks = links.map((l) => ({ kind: l.kind, labels: labels(l.en, l.ar) ?? {}, url: l.url.trim() }));
    const body = {
      ...definition,
      footer: organization || classification || footerLinks.length > 0 ? { organization, links: footerLinks, classification } : null,
      homeViews: views.length > 0
        ? views.map((v) => ({ id: v.id.trim(), labels: labels(v.en, v.ar) ?? {}, pageId: v.pageId || null, audienceRuleId: v.audienceRuleId || null }))
        : null,
    };
    const response = await fetch("/api/v1/experience/shell/definition", {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    });
    setSaving(false);
    if (response.ok) {
      setResult({ ok: true, messages: [] });
      router.refresh();
    } else {
      setResult({ ok: false, messages: await problemMessages(response) });
    }
  }

  return (
    <form className="shell-settings" onSubmit={save} noValidate>
      <section className="shell-settings-section" aria-labelledby="shell-settings-footer">
        <h2 id="shell-settings-footer">{t("footer.title")}</h2>
        <p className="shell-settings-hint">{t("footer.intro")}</p>
        <div className="shell-settings-grid">
          <FormField label={t("footer.organizationEn")} name="organizationEn" lang="en" dir="ltr" value={orgEn} onChange={(e) => setOrgEn(e.target.value)} />
          <FormField label={t("footer.organizationAr")} name="organizationAr" lang="ar" dir="rtl" value={orgAr} onChange={(e) => setOrgAr(e.target.value)} />
        </div>
        <div className="shell-settings-grid">
          <FormField label={t("footer.classificationEn")} name="classificationEn" lang="en" dir="ltr" value={classEn} onChange={(e) => setClassEn(e.target.value)} />
          <FormField label={t("footer.classificationAr")} name="classificationAr" lang="ar" dir="rtl" value={classAr} onChange={(e) => setClassAr(e.target.value)} />
        </div>
        <h3>{t("footer.links")}</h3>
        {links.length === 0 ? <p className="shell-settings-hint">{t("footer.noLinks")}</p> : null}
        <ol className="shell-settings-rows">
          {links.map((link, i) => {
            const number = i + 1;
            const titleId = `shell-settings-link-${link.key}`;
            return (
              <li key={link.key} className="shell-settings-row" role="group" aria-labelledby={titleId}>
                <div className="shell-settings-row-head">
                  <h4 id={titleId}>{t("footer.linkTitle", { number })}</h4>
                  <Button
                    variant="quiet"
                    size="sm"
                    icon="trash"
                    aria-label={t("footer.removeLink", { number })}
                    onClick={() => setLinks((rows) => rows.filter((r) => r.key !== link.key))}
                  >
                    {t("footer.remove")}
                  </Button>
                </div>
                <div className="shell-settings-grid">
                  <SelectField
                    label={t("footer.fields.kind")}
                    aria-label={t("footer.kind", { number })}
                    name={`linkKind${i}`}
                    value={link.kind}
                    onChange={(e) => updateLink(link.key, { kind: e.target.value as LinkKind })}
                    options={kinds.map((k) => ({ value: k, label: t(`footer.kinds.${k}`) }))}
                  />
                  <FormField
                    label={t("footer.fields.url")}
                    aria-label={t("footer.url", { number })}
                    hint={t("footer.urlHint")}
                    name={`linkUrl${i}`}
                    dir="ltr"
                    value={link.url}
                    onChange={(e) => updateLink(link.key, { url: e.target.value })}
                  />
                  <FormField
                    label={t("footer.fields.labelEn")}
                    aria-label={t("footer.labelEn", { number })}
                    name={`linkEn${i}`}
                    lang="en"
                    dir="ltr"
                    value={link.en}
                    onChange={(e) => updateLink(link.key, { en: e.target.value })}
                  />
                  <FormField
                    label={t("footer.fields.labelAr")}
                    aria-label={t("footer.labelAr", { number })}
                    name={`linkAr${i}`}
                    lang="ar"
                    dir="rtl"
                    value={link.ar}
                    onChange={(e) => updateLink(link.key, { ar: e.target.value })}
                  />
                </div>
              </li>
            );
          })}
        </ol>
        <Button icon="plus" onClick={() => setLinks((rows) => [...rows, { key: newKey(), kind: "privacy", en: "", ar: "", url: "" }])}>{t("footer.addLink")}</Button>
      </section>

      <section className="shell-settings-section" aria-labelledby="shell-settings-views">
        <h2 id="shell-settings-views">{t("views.title")}</h2>
        <p className="shell-settings-hint">{home ? t("views.intro") : t("views.needHome")}</p>
        {views.length === 0 ? <p className="shell-settings-hint">{t("views.none")}</p> : null}
        <ol className="shell-settings-rows">
          {views.map((view, i) => {
            const number = i + 1;
            const titleId = `shell-settings-view-${view.key}`;
            return (
              <li key={view.key} className="shell-settings-row" role="group" aria-labelledby={titleId}>
                <div className="shell-settings-row-head">
                  <h4 id={titleId}>{t("views.viewTitle", { number })}</h4>
                  <Button
                    variant="quiet"
                    size="sm"
                    icon="trash"
                    aria-label={t("views.remove", { number })}
                    onClick={() => setViews((rows) => rows.filter((r) => r.key !== view.key))}
                  >
                    {t("views.removeShort")}
                  </Button>
                </div>
                <div className="shell-settings-grid">
                  <FormField
                    label={t("views.fields.id")}
                    aria-label={t("views.id", { number })}
                    hint={t("views.idHint")}
                    name={`viewId${i}`}
                    dir="ltr"
                    value={view.id}
                    onChange={(e) => updateView(view.key, { id: e.target.value })}
                  />
                  <SelectField
                    label={t("views.fields.page")}
                    aria-label={t("views.page", { number })}
                    name={`viewPage${i}`}
                    value={view.pageId}
                    onChange={(e) => updateView(view.key, { pageId: e.target.value })}
                    options={pageOptions}
                  />
                  <FormField
                    label={t("views.fields.labelEn")}
                    aria-label={t("views.labelEn", { number })}
                    name={`viewEn${i}`}
                    lang="en"
                    dir="ltr"
                    value={view.en}
                    onChange={(e) => updateView(view.key, { en: e.target.value })}
                  />
                  <FormField
                    label={t("views.fields.labelAr")}
                    aria-label={t("views.labelAr", { number })}
                    name={`viewAr${i}`}
                    lang="ar"
                    dir="rtl"
                    value={view.ar}
                    onChange={(e) => updateView(view.key, { ar: e.target.value })}
                  />
                  {audiences !== null ? (
                    <SelectField
                      label={t("views.fields.audience")}
                      aria-label={t("views.audience", { number })}
                      name={`viewAudience${i}`}
                      value={view.audienceRuleId}
                      onChange={(e) => updateView(view.key, { audienceRuleId: e.target.value })}
                      options={audienceOptions}
                    />
                  ) : null}
                </div>
              </li>
            );
          })}
        </ol>
        {home ? <Button icon="plus" onClick={() => setViews((rows) => [...rows, { key: newKey(), id: "", en: "", ar: "", pageId: "", audienceRuleId: "" }])}>{t("views.add")}</Button> : null}
      </section>

      {result ? (
        <Alert live tone={result.ok ? "success" : "danger"} title={result.ok ? t("saved") : t("failed")}>
          {result.messages.length > 0 ? (
            <ul>
              {result.messages.map((m) => (
                <li key={m}>{m}</li>
              ))}
            </ul>
          ) : null}
        </Alert>
      ) : null}
      <div className="shell-settings-actions">
        <Button type="submit" variant="primary" disabled={saving}>
          {t("save")}
        </Button>
      </div>
    </form>
  );
}
