import { notFound, redirect } from "next/navigation";
import Link from "next/link";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { libraryHref, nameIn, type FolderView, type LibraryView, type SavedView, type SharedFolder } from "@/lib/documents/types";
import { isGuid, spaceFilesHref, spaceHref, spacesApi, spacesHref, type SpaceView } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { LibraryBrowser } from "@/features/documents/LibraryBrowser";
import { SpaceNav } from "@/features/spaces/parts";
import "@/features/documents/documents.css";

/** A space's files (spec 029 FR-006): its spec 011 library, with the space's members as the audience. */
export default async function SpaceFilesPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const space = await apiGet<SpaceView>(`${spacesApi}/${id}`);
  if (!space.ok) {
    notFound();
  }
  const t = await getTranslations("spaces");
  const libraryId = space.data.libraryId;
  const base = libraryId ? `/api/v1/documents/libraries/${libraryId}` : null;
  const [library, folders, views] = base
    ? await Promise.all([apiGet<LibraryView>(base), apiGet<FolderView[]>(`${base}/folders`), apiGet<SavedView[]>(`${base}/views`)])
    : [null, null, null];
  // Folders of other libraries shared with this space, read-only for its guests (spec 011 US8).
  const shared = await apiGet<SharedFolder[]>(`/api/v1/documents/shared-with-space/${id}`);
  const sharedFolders = shared.ok ? shared.data : [];
  const locale = await getLocale();
  const format = await getFormatter();
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
          { label: t("filesTitle"), href: spaceFilesHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("filesTitle")} eyebrow={space.data.name} icon="folder"
          nav={<SpaceNav id={id} current="files" settings={space.data.rights.owns || space.data.rights.manages} files={!!libraryId} discussion={!!space.data.communityId} />} />
        {library?.ok && folders?.ok && views?.ok ? (
          <LibraryBrowser library={library.data} folders={folders.data} views={views.data} />
        ) : !space.data.rights.reads ? (
          <StateView kind="denied" title={t("privateTitle")} message={t("private")} />
        ) : (
          sharedFolders.length === 0 ? <StateView kind="empty" title={t("filesUnavailableTitle")} message={t("filesUnavailable")} /> : null
        )}
        {sharedFolders.length > 0 ? (
          <section className="documents-card" aria-labelledby="shared-folders-title">
            <h2 id="shared-folders-title">{t("sharedFolders")}</h2>
            <ul className="documents-sharing-list">
              {sharedFolders.map((f) => (
                <li key={f.shareId} data-testid="shared-folder">
                  <Link href={`${libraryHref(f.libraryId)}?folder=${encodeURIComponent(f.folderId)}`}>
                    <bdi>{f.folderName}</bdi>
                  </Link>
                  <span className="documents-meta">
                    <bdi>{nameIn(f.libraryNames, locale)}</bdi>
                    {" · "}
                    {t("sharedUntil", { date: format.dateTime(new Date(f.expiresAt), { dateStyle: "medium" }) })}
                  </span>
                </li>
              ))}
            </ul>
          </section>
        ) : null}
      </main>
    </>
  );
}
