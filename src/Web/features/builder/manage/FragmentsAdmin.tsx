"use client";

import Link from "next/link";
import { useId, useState, type FormEvent } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { FormField } from "@/components/ui/FormField";
import {
  pagePath,
  unitName,
  type CatalogueTemplate,
  type Fragment,
  type FragmentImpact,
  type FragmentVersion,
  type OrgUnit,
  type PageSummary,
} from "@/lib/experience/builder-types";
import { useHydrated } from "@/lib/clock";
import { SendMessages, useSend } from "./useSend";

type Source = { pageId: string; regionId: string };

function fragmentName(f: Fragment, locale: string): string {
  return f.names[locale] ?? f.names.en ?? Object.values(f.names)[0] ?? f.key;
}

/** Picks the page region whose draft placements become the fragment's content. */
function SourcePicker({ pages, templates, value, onChange }: { pages: PageSummary[]; templates: CatalogueTemplate[]; value: Source; onChange: (s: Source) => void }) {
  const t = useTranslations("builder.manage.fragments");
  const tr = useTranslations("builder.regions");
  const pageId = useId();
  const regionId = useId();
  const page = pages.find((p) => p.id === value.pageId);
  const regions = page ? (templates.find((x) => x.id === page.templateId)?.regions ?? []) : [];
  return (
    <div className="builder-manage-row">
      <div className="field">
        <label htmlFor={pageId}>{t("fromPage")}</label>
        <select id={pageId} value={value.pageId} onChange={(e) => onChange({ pageId: e.target.value, regionId: "" })} required>
          <option value="">{t("choose")}</option>
          {pages.map((p) => (
            <option key={p.id} value={p.id}>
              {pagePath(p.slug)}
            </option>
          ))}
        </select>
      </div>
      <div className="field">
        <label htmlFor={regionId}>{t("fromRegion")}</label>
        <select id={regionId} value={value.regionId} disabled={!page} onChange={(e) => onChange({ ...value, regionId: e.target.value })} required>
          <option value="">{t("choose")}</option>
          {regions.map((r) => (
            <option key={r.id} value={r.id}>
              {tr(r.id)}
            </option>
          ))}
        </select>
      </div>
    </div>
  );
}

/**
 * Fragments (spec 032 R2 T027, FR-045): a reusable group of placements, made from a page region's draft, that every page
 * placing it shows in its current version. A new version comes from a page region again; earlier versions stay readable.
 * Who may manage a fragment, at which scope, and every content rule are the API's.
 */
export function FragmentsAdmin({
  initial,
  units,
  pages,
  templates,
}: {
  initial: Fragment[];
  units: OrgUnit[];
  pages: PageSummary[];
  templates: CatalogueTemplate[];
}) {
  const t = useTranslations("builder.manage.fragments");
  const locale = useLocale();
  const format = useFormatter();
  const hydrated = useHydrated();
  const ids = { title: useId(), unit: useId(), detail: useId() };
  const [fragments, setFragments] = useState(initial);
  const [key, setKey] = useState("");
  const [nameEn, setNameEn] = useState("");
  const [nameAr, setNameAr] = useState("");
  const [unit, setUnit] = useState("");
  const [source, setSource] = useState<Source>({ pageId: "", regionId: "" });
  const [open, setOpen] = useState<Fragment | null>(null);
  const [versions, setVersions] = useState<FragmentVersion[] | null>(null);
  const [impact, setImpact] = useState<FragmentImpact | null>(null);
  const [next, setNext] = useState<Source>({ pageId: "", regionId: "" });
  const [note, setNote] = useState("");
  const { busy, result, send } = useSend(t("failed"));

  const scopeName = (id: string | null) =>
    id === null ? t("everyPage") : (units.find((u) => u.id === id) ? unitName(units.find((u) => u.id === id)!, locale) : t("unknownUnit"));
  const when = (iso: string) => (hydrated ? format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" }) : "");

  async function reload() {
    const response = await fetch("/api/v1/experience/fragments", { cache: "no-store" });
    if (response.ok) setFragments((await response.json()) as Fragment[]);
  }

  async function show(f: Fragment) {
    setOpen(f);
    setVersions(null);
    setImpact(null);
    setNext({ pageId: "", regionId: "" });
    setNote("");
    const [v, i] = await Promise.all([
      fetch(`/api/v1/experience/fragments/${f.id}/versions`, { cache: "no-store" }).catch(() => null),
      fetch(`/api/v1/experience/fragments/${f.id}/impact`, { cache: "no-store" }).catch(() => null),
    ]);
    if (v?.ok) setVersions((await v.json()) as FragmentVersion[]);
    if (i?.ok) setImpact((await i.json()) as FragmentImpact);
  }

  async function create(e: FormEvent) {
    e.preventDefault();
    const names: Record<string, string> = {};
    if (nameEn.trim()) names.en = nameEn.trim();
    if (nameAr.trim()) names.ar = nameAr.trim();
    const made = await send<Fragment>(
      "/api/v1/experience/fragments",
      "POST",
      { key, names, orgUnitId: unit || null, fromPageId: source.pageId || null, regionId: source.regionId || null },
      t("created"),
    );
    if (made) {
      await reload();
      setKey("");
      setNameEn("");
      setNameAr("");
      setSource({ pageId: "", regionId: "" });
    }
  }

  async function update(e: FormEvent) {
    e.preventDefault();
    if (!open) return;
    const saved = await send<Fragment>(
      `/api/v1/experience/fragments/${open.id}`,
      "PUT",
      { fromPageId: next.pageId || null, regionId: next.regionId || null, note: note.trim() || null },
      t("updated"),
    );
    if (saved) {
      await reload();
      await show(saved);
    }
  }

  const columns: Column<Fragment>[] = [
    { key: "name", header: t("name"), rowHeader: true, cell: (f) => <bdi>{fragmentName(f, locale)}</bdi> },
    { key: "key", header: t("key"), priority: "low", cell: (f) => <bdi dir="ltr">{f.key}</bdi> },
    { key: "scope", header: t("scope"), cell: (f) => <bdi>{scopeName(f.orgUnitId)}</bdi> },
    { key: "version", header: t("version"), align: "end", cell: (f) => String(f.currentVersion) },
    {
      key: "state",
      header: t("state"),
      cell: (f) => <Badge tone={f.archivedAt ? "neutral" : "success"}>{f.archivedAt ? t("archived") : t("active")}</Badge>,
    },
    {
      key: "manage",
      header: t("manage"),
      align: "end",
      cell: (f) => (
        <Button variant="quiet" size="sm" aria-label={t("openLabel", { name: fragmentName(f, locale) })} onClick={() => void show(f)}>
          {t("open")}
        </Button>
      ),
    },
  ];

  return (
    <div className="builder-manage" data-testid="fragments-admin">
      <DataTable
        caption={t("caption")}
        columns={columns}
        rows={fragments}
        rowKey={(f) => f.id}
        emptyTitle={t("emptyTitle")}
        emptyMessage={t("empty")}
        density="compact"
      />

      {open ? (
        <section className="builder-manage-form" aria-labelledby={ids.detail} data-testid="fragment-detail">
          <h2 id={ids.detail}>{fragmentName(open, locale)}</h2>
          <dl className="builder-facts">
            <dt>{t("reference")}</dt>
            <dd>
              <bdi dir="ltr">{open.reference}</bdi>
            </dd>
            <dt>{t("placements")}</dt>
            <dd>{String(open.placements.length)}</dd>
            <dt>{t("usedOn")}</dt>
            <dd data-testid="fragment-impact">{impact ? t("pageCount", { count: impact.pageCount }) : t("loading")}</dd>
          </dl>
          {impact && impact.pages.length > 0 ? (
            <ul className="builder-manage-list">
              {impact.pages.map((p) => (
                <li key={p.id}>
                  <Link href={`/admin/pages/${p.id}`}>
                    <bdi dir="ltr">{pagePath(p.slug)}</bdi>
                  </Link>
                </li>
              ))}
            </ul>
          ) : null}
          <h3>{t("versions")}</h3>
          {versions ? (
            <ol className="builder-manage-list" data-testid="fragment-versions">
              {versions.map((v) => (
                <li key={v.version}>
                  {t("versionLine", { version: String(v.version), when: when(v.createdAt), count: v.placements.length })}
                  {v.note ? (
                    <>
                      {" "}
                      <bdi>{v.note}</bdi>
                    </>
                  ) : null}
                </li>
              ))}
            </ol>
          ) : (
            <p className="field-hint">{t("loading")}</p>
          )}
          {open.archivedAt === null ? (
            <form onSubmit={(e) => void update(e)} data-testid="fragment-update">
              <h3>{t("newVersion")}</h3>
              <p className="field-hint">{t("newVersionHint")}</p>
              <SourcePicker pages={pages} templates={templates} value={next} onChange={setNext} />
              <FormField label={t("note")} name="fragment-note" value={note} maxLength={200} onChange={(e) => setNote(e.target.value)} />
              <div className="button-row">
                <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
                  {t("saveVersion")}
                </Button>
                <ConfirmAction
                  label={t("archive")}
                  question={t("archiveQuestion", { name: fragmentName(open, locale) })}
                  confirmLabel={t("archiveConfirm")}
                  cancelLabel={t("cancel")}
                  onConfirm={async () => {
                    if (await send(`/api/v1/experience/fragments/${open.id}`, "DELETE", null, t("archivedDone"))) {
                      await reload();
                      setOpen(null);
                    }
                  }}
                />
                <Button variant="secondary" onClick={() => setOpen(null)}>
                  {t("close")}
                </Button>
              </div>
            </form>
          ) : (
            <Button variant="secondary" onClick={() => setOpen(null)}>
              {t("close")}
            </Button>
          )}
        </section>
      ) : null}

      <form className="builder-manage-form" aria-labelledby={ids.title} onSubmit={(e) => void create(e)} data-testid="fragment-form">
        <h2 id={ids.title}>{t("addTitle")}</h2>
        <p className="field-hint">{t("addHint")}</p>
        <div className="builder-manage-row">
          <FormField label={t("key")} name="fragment-key" value={key} hint={t("keyHint")} dir="ltr" maxLength={40} onChange={(e) => setKey(e.target.value)} required />
          <div className="field">
            <label htmlFor={ids.unit}>{t("scope")}</label>
            <select id={ids.unit} value={unit} onChange={(e) => setUnit(e.target.value)}>
              <option value="">{t("everyPage")}</option>
              {units.map((u) => (
                <option key={u.id} value={u.id}>
                  {unitName(u, locale)}
                </option>
              ))}
            </select>
          </div>
        </div>
        <div className="builder-manage-row">
          <FormField label={t("nameEn")} name="fragment-name-en" value={nameEn} lang="en" dir="ltr" maxLength={120} onChange={(e) => setNameEn(e.target.value)} required />
          <FormField label={t("nameAr")} name="fragment-name-ar" value={nameAr} lang="ar" dir="rtl" maxLength={120} onChange={(e) => setNameAr(e.target.value)} />
        </div>
        <SourcePicker pages={pages} templates={templates} value={source} onChange={setSource} />
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
          {t("add")}
        </Button>
      </form>
      <SendMessages result={result} />
    </div>
  );
}
