import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import Link from "next/link";
import {
  libraryHref,
  libraryImportHref,
  nameIn,
  type FolderView,
  type GuestShare,
  type LibraryView,
  type SavedView,
} from "@/lib/documents/types";
import { Header } from "@/components/shell/Header";
import { LibraryBrowser } from "@/features/documents/LibraryBrowser";
import { LibrarySharing } from "@/features/documents/LibrarySharing";
import { ConnectedLibraryBrowser, type TrailStep } from "@/features/documents/ConnectedLibraryBrowser";

/** The folder path of a connected library from the address; anything that is not a short list of steps is ignored. */
function readTrail(value: string | string[] | undefined): TrailStep[] {
  if (typeof value !== "string" || value.length > 8000) return [];
  try {
    const parsed: unknown = JSON.parse(value);
    if (!Array.isArray(parsed) || parsed.length > 30) return [];
    return parsed.every((s) => typeof s?.id === "string" && typeof s?.name === "string")
      ? parsed.map((s: TrailStep) => ({ id: s.id, name: s.name }))
      : [];
  } catch {
    return [];
  }
}

/** One library (spec 011 US1): folders, filters on the type's fields, sorting, saved views and upload. */
export default async function LibraryPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { id } = await params;
  const query = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const base = `/api/v1/documents/libraries/${encodeURIComponent(id)}`;
  const [library, folders, views] = await Promise.all([
    apiGet<LibraryView>(base),
    apiGet<FolderView[]>(`${base}/folders`),
    apiGet<SavedView[]>(`${base}/views`),
  ]);
  if (!library.ok || !folders.ok || !views.ok) {
    notFound();
  }
  const connected = library.data.connected != null;
  // Download and sharing controls (spec 011 US8) are for the managers of TeamBeats libraries outside spaces.
  const sharing =
    library.data.rights.manage && library.data.scope !== "space" && !connected
      ? await Promise.all([
          apiGet<GuestShare[]>(`${base}/guest-shares`),
          apiGet<{ id: string; name: string; status: string }[]>(
            "/api/v1/spaces?scope=mine",
          ),
        ])
      : null;
  const t = await getTranslations("documents.libraries");
  const locale = await getLocale();
  const name = nameIn(library.data.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/documents" },
          { label: name, href: libraryHref(id) },
        ]}
      />
      <main id="main" className="page">
        <h1>
          <bdi>{name}</bdi>
        </h1>
        {library.data.rights.manage && !connected ? (
          <p>
            <Link href={libraryImportHref(id)}>{t("importLink")}</Link>
          </p>
        ) : null}
        {connected ? (
          <ConnectedLibraryBrowser key={JSON.stringify(query.trail ?? "")} library={library.data} path={libraryHref(id)} trail={readTrail(query.trail)} />
        ) : (
          <LibraryBrowser
            library={library.data}
            folders={folders.data}
            views={views.data}
          />
        )}
        {sharing ? (
          <LibrarySharing
            library={library.data}
            folders={folders.data}
            shares={sharing[0].ok ? sharing[0].data : []}
            spaces={
              sharing[1].ok
                ? sharing[1].data
                    .filter((s) => s.status === "active")
                    .map((s) => ({ id: s.id, name: s.name }))
                : []
            }
          />
        ) : null}
      </main>
    </>
  );
}
