import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  libraryHref,
  nameIn,
  type ConnectedSource,
  type LibrarySummary,
} from "@/lib/documents/types";
import type { OrgUnitView } from "@/lib/admin/types";
import { ConnectLibraryForm } from "@/features/documents/ConnectLibraryForm";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { PageHeader } from "@/components/ui/PageHeader";
import { TileCard, type TileTone } from "@/components/ui/TileCard";
import { recordsHref } from "@/lib/records/types";
import { assetsHref } from "@/lib/assets/types";
import { ButtonLink } from "@/components/ui/Button";
import { FilterToolbar, ToolbarSearch, ToolbarSelect } from "@/components/ui/FilterToolbar";

const scopes = ["tenant", "org_unit", "site", "space"] as const;

const tones: readonly TileTone[] = ["accent", "highlight", "success", "info"];

/**
 * The document libraries this person may open (spec 011 US1), at tenant, unit and site scope. The page opens with
 * search (spec 036 DLS-106): the box narrows the libraries here and offers the search inside the files.
 */
export default async function LibrariesPage({ searchParams }: { searchParams: Promise<{ q?: string; scope?: string }> }) {
  const params = await searchParams;
  const q = (params.q ?? "").trim().slice(0, 200);
  const scope = scopes.find((k) => k === params.scope);
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [libraries, records, assets, sources, units] = await Promise.all([
    apiGet<LibrarySummary[]>("/api/v1/documents/libraries"),
    // Records managers get a way in; the API decides who that is.
    apiGet<unknown[]>("/api/v1/records/disposition"),
    // The brand asset portal, where the plan has it (spec 011 US5).
    apiGet<unknown[]>("/api/v1/assets/collections"),
    // People who manage libraries somewhere can connect SharePoint libraries (spec 011 US9); the API decides who.
    apiGet<ConnectedSource[]>("/api/v1/documents/connected-sources"),
    apiGet<OrgUnitView[]>("/api/v1/org/units"),
  ]);
  if (!libraries.ok) {
    notFound();
  }
  const t = await getTranslations("documents.libraries");
  const locale = await getLocale();
  const needle = q.toLocaleLowerCase(locale);
  const shown = libraries.data.filter((l) => (!scope || l.scope === scope) && (!needle || nameIn(l.names, locale).toLocaleLowerCase(locale).includes(needle)));
  const present = scopes.filter((k) => libraries.data.some((l) => l.scope === k));
  const href = (keep: { q?: string; scope?: string }) => {
    const next = new URLSearchParams();
    if (keep.q) next.set("q", keep.q);
    if (keep.scope) next.set("scope", keep.scope);
    return next.size > 0 ? `/documents?${next}` : "/documents";
  };
  const inside = q ? (
    <ButtonLink href={`/search?${new URLSearchParams({ q, kind: "document" })}`} icon="search">
      {t("searchInside", { q })}
    </ButtonLink>
  ) : undefined;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[{ label: t("title"), href: "/documents" }]}
      />
      <main id="main" className="page">
        <PageHeader
          title={t("title")}
          description={t("intro")}
          icon="files"
          actions={
            assets.ok || records.ok ? (
              <>
                {assets.ok ? <ButtonLink href={assetsHref}>{t("assetsLink")}</ButtonLink> : null}
                {records.ok ? <ButtonLink href={recordsHref}>{t("recordsLink")}</ButtonLink> : null}
              </>
            ) : null
          }
        />
        {libraries.data.length > 0 ? (
          <FilterToolbar
            label={t("search")}
            method="get"
            search={<ToolbarSearch label={t("search")} name="q" placeholder={t("searchHint")} defaultValue={q} />}
            end={
              <>
                <button type="submit" className="button button-secondary">
                  {t("apply")}
                </button>
                {shown.length > 0 ? inside : null}
              </>
            }
            applied={[
              ...(q ? [{ key: "q", label: q, href: href({ scope }) }] : []),
              ...(scope ? [{ key: "scope", label: t(`scope.${scope}`), href: href({ q }) }] : []),
            ]}
            clearHref="/documents"
          >
            {present.length > 1 ? (
              <ToolbarSelect label={t("scopeFilter")} hideLabel name="scope" defaultValue={scope ?? ""}>
                <option value="">{t("anyScope")}</option>
                {present.map((k) => (
                  <option key={k} value={k}>
                    {t(`scope.${k}`)}
                  </option>
                ))}
              </ToolbarSelect>
            ) : null}
          </FilterToolbar>
        ) : null}
        {libraries.data.length === 0 ? (
          <StateView kind="empty" title={t("none")} message="" />
        ) : shown.length === 0 ? (
          <StateView kind="empty" icon="search" title={t("noMatch")} message={t("noMatchHint")} action={inside ?? <ButtonLink href="/documents">{t("clear")}</ButtonLink>} />
        ) : (
          <ul className="ui-tiles" data-testid="library-list">
            {shown.map((l, i) => (
              <li key={l.id}>
                <TileCard
                  title={nameIn(l.names, locale)}
                  href={libraryHref(l.id)}
                  icon="files"
                  tone={tones[i % tones.length]}
                  meta={l.connected ? t("connectedMeta", { scope: t(`scope.${l.scope}`) }) : t(`scope.${l.scope}`)}
                />
              </li>
            ))}
          </ul>
        )}
        {sources.ok && sources.data.length > 0 ? (
          <ConnectLibraryForm
            sources={sources.data}
            scopes={[
              { value: "tenant", label: t("scope.tenant") },
              ...(units.ok
                ? units.data
                    .filter((u) => u.status === "active")
                    .map((u) => ({ value: `org_unit|${u.id}`, label: nameIn(u.names, locale) }))
                : []),
            ]}
          />
        ) : null}
      </main>
    </>
  );
}
