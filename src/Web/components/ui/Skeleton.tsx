import { useTranslations } from "next-intl";

/**
 * Loading skeleton sized like the content it stands for (FR-045). The shimmer is decorative and stops under reduced
 * motion; the group announces loading once instead of per line.
 */
export function Skeleton({ lines = [] }: { lines?: string[] }) {
  const t = useTranslations("ui.state.loading");
  return (
    <div className="ui-skeleton-group" aria-busy="true" role="status">
      <span className="visually-hidden">{t("title")}</span>
      {lines.map((size, i) => (
        <span key={i} className="ui-skeleton" aria-hidden="true" style={{ blockSize: size }} />
      ))}
    </div>
  );
}
