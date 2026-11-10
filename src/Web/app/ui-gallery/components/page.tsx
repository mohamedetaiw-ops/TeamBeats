import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { ComponentsGallery } from "./ComponentsGallery";

/**
 * Gallery of the extended component catalogue (spec 036 DLS-031 to DLS-038). Like the first gallery page it exists
 * only where the development fixtures are on, and the design suite runs against it in every design and both directions.
 */
export default async function ComponentsGalleryPage() {
  if (process.env.NEXT_PUBLIC_TB_REFERENCE_PACKS !== "true") {
    notFound();
  }
  const t = await getTranslations("components_gallery");
  return (
    <main id="main" className="page">
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      <ComponentsGallery />
    </main>
  );
}
