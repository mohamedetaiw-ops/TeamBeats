import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  libraryHref,
  nameIn,
  type LibrarySummary,
} from "@/lib/documents/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { PageHeader } from "@/components/ui/PageHeader";
import { TileCard, type TileTone } from "@/components/ui/TileCard";
import { recordsHref } from "@/lib/records/types";
import { assetsHref } from "@/lib/assets/types";

const tones: readonly TileTone[] = ["accent", "highlight", "success", "info"];

/** The document libraries this person may open (spec 011 US1), at tenant, unit and site scope. */
export default async function LibrariesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [libraries, records, assets] = await Promise.all([
    apiGet<LibrarySummary[]>("/api/v1/documents/libraries"),
    // Records managers get a way in; the API decides who that is.
    apiGet<unknown[]>("/api/v1/records/disposition"),
    // The brand asset portal, where the plan has it (spec 011 US5).
    apiGet<unknown[]>("/api/v1/assets/collections"),
  ]);
  if (!libraries.ok) {
    notFound();
  }
  const t = await getTranslations("documents.libraries");
  const locale = await getLocale();
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[{ label: t("title"), href: "/documents" }]}
      />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="files" />
        {assets.ok ? (
          <p>
            <Link href={assetsHref}>{t("assetsLink")}</Link>
          </p>
        ) : null}
        {records.ok ? (
          <p>
            <Link href={recordsHref}>{t("recordsLink")}</Link>
          </p>
        ) : null}
        {libraries.data.length === 0 ? (
          <StateView kind="empty" title={t("none")} message="" />
        ) : (
          <ul className="ui-tiles" data-testid="library-list">
            {libraries.data.map((l, i) => (
              <li key={l.id}>
                <TileCard title={nameIn(l.names, locale)} href={libraryHref(l.id)} icon="files" tone={tones[i % tones.length]} meta={t(`scope.${l.scope}`)} />
              </li>
            ))}
          </ul>
        )}
      </main>
    </>
  );
}
