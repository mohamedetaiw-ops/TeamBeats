import type { CSSProperties, ReactNode } from "react";
import { EventChannel } from "@/lib/experience/events";
import type { RenderedPage, RenderedPlacement, RenderedRegion, SectionMode } from "@/lib/experience/types";
import { Placement } from "./Placement";
import { RegionTabs } from "./RegionTabs";
import "./experience.css";

type Row = { section: SectionMode; regions: RenderedRegion[] };

/** Consecutive regions of the same section mode share a row while their spans fit the 12 columns. */
function rows(regions: RenderedRegion[]): Row[] {
  const result: Row[] = [];
  let used = 0;
  for (const region of regions) {
    const last = result.at(-1);
    if (last && last.section === region.section && used + region.span <= 12) {
      last.regions.push(region);
      used += region.span;
    } else {
      result.push({ section: region.section, regions: [region] });
      used = region.span;
    }
  }
  return result;
}

/**
 * Optional controls drawn next to regions and placements, such as personalization mode (spec 008). The renderer only
 * gives them a place; it never knows what they do.
 */
export type RendererTools = {
  region?: (region: RenderedRegion, index: number, page: RenderedPage) => ReactNode;
  placement?: (placement: RenderedPlacement, region: RenderedRegion, index: number) => ReactNode;
};

function Region({ region, index, page, tools }: { region: RenderedRegion; index: number; page: RenderedPage; tools?: RendererTools }) {
  return (
    <div className="xp-region" data-region={region.id} style={{ "--xp-region-span": region.span } as CSSProperties}>
      {tools?.region?.(region, index, page)}
      <div className="xp-region-grid">
        {region.placements.map((placement, i) => (
          <Placement key={placement.id} placement={{ ...placement, span: Math.min(placement.span, region.span) }} tools={tools?.placement?.(placement, region, i)} />
        ))}
      </div>
    </div>
  );
}

/**
 * Draws a page from its effective configuration (spec 032 FR-010 to FR-013): a 12-column CSS grid per row, regions
 * as size containers so placements adapt to the space they get, logical properties only, and the layout's fallback
 * for narrow containers. Every placement is its own boundary; the page never knows which components it holds.
 */
export function PageRenderer({
  page,
  tabLabel,
  tabsLabel,
  tools,
}: {
  page: RenderedPage;
  tabLabel: (n: number) => string;
  tabsLabel: string;
  tools?: RendererTools;
}) {
  const index = (region: RenderedRegion) => page.regions.indexOf(region);
  const body =
    page.fallback === "tabs" ? (
      <div className="xp-row xp-section-contained">
        <RegionTabs label={tabsLabel} tabs={page.regions.map((r, i) => ({ id: r.id, label: tabLabel(i + 1) }))}>
          {page.regions.map((region) => (
            <Region key={region.id} region={region} index={index(region)} page={page} tools={tools} />
          ))}
        </RegionTabs>
      </div>
    ) : (
      rows(page.regions).map((row) => (
        <div key={row.regions[0]!.id} className={`xp-row xp-section-${row.section}`}>
          <div className="xp-row-grid">
            {row.regions.map((region) => (
              <Region key={region.id} region={region} index={index(region)} page={page} tools={tools} />
            ))}
          </div>
        </div>
      ))
    );
  return (
    <EventChannel>
      <div className={`xp-page xp-variant-${page.variant} xp-fallback-${page.fallback}`} data-layout={page.layout} data-template={page.template}>
        {body}
      </div>
    </EventChannel>
  );
}
