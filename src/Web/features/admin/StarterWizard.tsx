"use client";

import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField } from "@/components/ui/FormField";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { unitLabel, type OrgUnitView, type UserSummary } from "@/lib/admin/types";
import { ResultAlert, useAdminAction } from "./shared";

export type StarterTemplate = { id: string; name: string; description: string };
export type StarterBrand = { id: string; name: string };
type StarterPage = { id: string; slug: string; nodeId: string; published: boolean };
type Starter = { id: string; orgUnitId: string; state: "draft" | "published"; brandId: string; pages: StarterPage[] };

const DESIGNS = ["beats", "atlas", "pulse", "studio"] as const;
const NAVIGATION = ["rail", "top", "sidebar"] as const;

/** A suggested address: the organization's code and the template, which the administrator can change. */
function suggestedSlug(unit: OrgUnitView | undefined, template: string): string {
  const base = (unit?.code ?? "org").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "") || "org";
  return `${base}-${template.toLowerCase()}`;
}

/**
 * The starter wizard for a new organization (spec 033 R2 T020, US6, FR-027): a brand to start from, the design, the
 * navigation pattern, the language and starter pages from templates, and the person who will run the organization. What it
 * makes is a draft that people do not see until it is published, and each part opens in its own editor. The API checks
 * every choice and permission; this screen only collects them and shows the answer.
 */
export function StarterWizard({
  units,
  users,
  brands,
  templates,
  locales,
  initialUnit,
}: {
  units: OrgUnitView[];
  users: UserSummary[];
  brands: StarterBrand[];
  templates: StarterTemplate[];
  locales: string[];
  initialUnit: string;
}) {
  const t = useTranslations("admin.org.starter");
  const tl = useTranslations("admin.org.starter.languages");
  const locale = useLocale();
  const { busy, result, run } = useAdminAction("/admin/organization/starter");
  const [unit, setUnit] = useState(initialUnit);
  // The starter read for one organization; another organization's answer never shows for the one chosen now.
  const [loaded, setLoaded] = useState<{ unit: string; starter: Starter | null } | null>(null);
  const starter = loaded && loaded.unit === unit ? loaded.starter : undefined;
  const setStarter = (s: Starter) => setLoaded({ unit, starter: s });
  const [fromBrand, setFromBrand] = useState("");
  const [design, setDesign] = useState<string>("beats");
  const [navigation, setNavigation] = useState<string>("rail");
  const [language, setLanguage] = useState(locales.includes(locale) ? locale : (locales[0] ?? "en"));
  const [admin, setAdmin] = useState("");
  const [chosen, setChosen] = useState<Record<string, { slug: string; title: string }>>({});

  const unitView = units.find((u) => u.id === unit);
  const activeUnits = units.filter((u) => u.status === "active");
  const people = users.filter((u) => u.membershipStatus === "active");

  useEffect(() => {
    if (unit === "") return;
    let live = true;
    void fetch(`/api/v1/experience/starters/for/${unit}`, { cache: "no-store" })
      .then(async (r) => (r.status === 200 ? ((await r.json()) as Starter) : r.status === 204 ? null : undefined))
      .catch(() => undefined)
      .then((s) => {
        if (live) setLoaded({ unit, starter: s ?? null });
      });
    return () => {
      live = false;
    };
  }, [unit]);

  function toggle(template: StarterTemplate, on: boolean) {
    const next = { ...chosen };
    if (on) next[template.id] = { slug: suggestedSlug(unitView, template.id), title: template.name };
    else delete next[template.id];
    setChosen(next);
  }

  async function create() {
    const outcome = await run<Starter>(
      "create",
      "POST",
      "/api/v1/experience/starters",
      {
        orgUnitId: unit,
        fromBrandId: fromBrand === "" ? null : fromBrand,
        design,
        navigation,
        language,
        adminUserId: admin === "" ? null : admin,
        pages: templates.filter((tp) => chosen[tp.id]).map((tp) => ({ templateId: tp.id, slug: chosen[tp.id]!.slug, title: chosen[tp.id]!.title })),
      },
      t("created"),
    );
    if (outcome.ok) setStarter(outcome.data);
  }

  async function publish() {
    if (!starter) return;
    const outcome = await run<Starter>("publish", "POST", `/api/v1/experience/starters/${starter.id}/publish`, undefined, t("publishedDone"));
    if (outcome.ok) setStarter(outcome.data);
  }

  const pageCount = Object.keys(chosen).length;
  return (
    <div className="admin-screen">
      <ResultAlert result={result} />
      <section className="admin-panel" aria-labelledby="starter-unit-title">
        <div className="admin-panel-head">
          <h2 id="starter-unit-title">{t("organizationTitle")}</h2>
        </div>
        <p className="admin-meta">{t("intro")}</p>
        <SelectField
          label={t("organization")}
          name="organization"
          value={unit}
          onChange={(e) => setUnit(e.target.value)}
          options={[{ value: "", label: t("chooseOrganization") }, ...activeUnits.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
        />
      </section>

      {unit !== "" && starter ? (
        <section className="admin-panel" aria-labelledby="starter-made-title" data-testid="starter-made">
          <div className="admin-panel-head">
            <h2 id="starter-made-title">{t("madeTitle")}</h2>
            <Badge tone={starter.state === "published" ? "success" : "warning"}>{t(`state.${starter.state}`)}</Badge>
          </div>
          <p className="admin-meta">{starter.state === "published" ? t("madePublished") : t("madeDraft")}</p>
          <DataTable
            caption={t("madeTitle")}
            captionHidden
            rows={starter.pages}
            rowKey={(p) => p.id}
            columns={[
              { key: "page", header: t("page"), rowHeader: true, cell: (p) => <bdi dir="ltr">{p.slug}</bdi> },
              {
                key: "state",
                header: t("pageState"),
                nowrap: true,
                cell: (p) => <Badge tone={p.published ? "success" : "neutral"}>{p.published ? t("pagePublished") : t("pageDraft")}</Badge>,
              },
              {
                key: "open",
                header: t("actions"),
                cell: (p) => (
                  <ButtonLink href={`/admin/pages/${encodeURIComponent(p.id)}`} variant="secondary" icon="edit">
                    {t("editPage")}
                  </ButtonLink>
                ),
              },
            ]}
          />
          <div className="ui-control-row">
            <ButtonLink href={`/admin/branding?brand=${encodeURIComponent(starter.brandId)}`} variant="secondary" icon="edit">
              {t("editBrand")}
            </ButtonLink>
            {starter.state === "draft" ? (
              <Button variant="primary" disabled={busy !== null} onClick={() => void publish()}>
                {t("publish")}
              </Button>
            ) : null}
          </div>
        </section>
      ) : null}

      {unit !== "" && starter === null ? (
        <section className="admin-panel" aria-labelledby="starter-form-title">
          <div className="admin-panel-head">
            <h2 id="starter-form-title">{t("formTitle")}</h2>
          </div>
          <SelectField
            label={t("fromBrand")}
            hint={t("fromBrandHint")}
            name="fromBrand"
            value={fromBrand}
            onChange={(e) => setFromBrand(e.target.value)}
            options={[{ value: "", label: t("platformDefaults") }, ...brands.map((b) => ({ value: b.id, label: b.name }))]}
          />
          <RadioGroup
            legend={t("design")}
            name="design"
            cards
            value={design}
            onChange={(e) => setDesign(e.target.value)}
            options={DESIGNS.map((d) => ({ value: d, label: t(`designs.${d}`), hint: t(`designHints.${d}`) }))}
          />
          <RadioGroup
            legend={t("navigation")}
            name="navigation"
            value={navigation}
            onChange={(e) => setNavigation(e.target.value)}
            options={NAVIGATION.map((n) => ({ value: n, label: t(`navigationPatterns.${n}`) }))}
          />
          <RadioGroup
            legend={t("language")}
            hint={t("languageHint")}
            name="language"
            value={language}
            onChange={(e) => setLanguage(e.target.value)}
            options={locales.map((l) => ({ value: l, label: tl.has(l as never) ? tl(l as never) : l }))}
          />
          <fieldset className="admin-fieldset">
            <legend>{t("pages")}</legend>
            <p className="admin-meta">{t("pagesHint")}</p>
            <div className="admin-choices">
              {templates.map((tp) => (
                <div key={tp.id}>
                  <Checkbox card label={tp.name} hint={tp.description} checked={Boolean(chosen[tp.id])} onChange={(e) => toggle(tp, e.target.checked)} />
                  {chosen[tp.id] ? (
                    <div className="admin-fields">
                      <FormField
                        label={t("pageTitle", { template: tp.name })}
                        name={`title-${tp.id}`}
                        value={chosen[tp.id]!.title}
                        onChange={(e) => setChosen({ ...chosen, [tp.id]: { ...chosen[tp.id]!, title: e.target.value } })}
                      />
                      <FormField
                        label={t("pageSlug", { template: tp.name })}
                        name={`slug-${tp.id}`}
                        dir="ltr"
                        value={chosen[tp.id]!.slug}
                        onChange={(e) => setChosen({ ...chosen, [tp.id]: { ...chosen[tp.id]!, slug: e.target.value } })}
                      />
                    </div>
                  ) : null}
                </div>
              ))}
            </div>
          </fieldset>
          <SelectField
            label={t("admin")}
            hint={t("adminHint")}
            name="admin"
            value={admin}
            onChange={(e) => setAdmin(e.target.value)}
            options={[{ value: "", label: t("adminSelf") }, ...people.map((u) => ({ value: u.id, label: u.displayName }))]}
          />
          <p className="admin-meta">{t("draftNote")}</p>
          <div className="ui-control-row">
            <Button variant="primary" disabled={busy !== null || pageCount === 0} onClick={() => void create()}>
              {t("create")}
            </Button>
          </div>
        </section>
      ) : null}
    </div>
  );
}
