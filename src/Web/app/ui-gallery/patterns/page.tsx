import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { PatternsGallery } from "./PatternsGallery";

/**
 * Gallery of the page patterns (spec 036 T021): dashboard, assistant chat, agent profile, record profile and board, each
 * built only from the shared components. Like the other gallery pages it exists only where the development fixtures are
 * on, and the design suite runs against it in every design and both directions.
 */
export default async function PatternsGalleryPage() {
  if (process.env.NEXT_PUBLIC_TB_REFERENCE_PACKS !== "true") {
    notFound();
  }
  const t = await getTranslations("patterns_gallery");
  return (
    <main id="main" className="page">
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      <PatternsGallery />
    </main>
  );
}
