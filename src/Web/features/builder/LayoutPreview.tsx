import type { CatalogueLayout } from "@/lib/experience/builder-types";

/**
 * A schematic of a layout: one box per region at its column span. It is decoration only; the region list next to it
 * says the same thing in text (FR-017), so it is hidden from assistive technology.
 */
export function LayoutPreview({ layout, regionName }: { layout: CatalogueLayout; regionName: (id: string) => string }) {
  return (
    <div className="builder-preview" aria-hidden="true">
      {layout.regions.map((r) => (
        <span key={r.id} className="builder-preview-region" data-span={r.span} data-section={r.section}>
          {regionName(r.id)}
        </span>
      ))}
    </div>
  );
}
