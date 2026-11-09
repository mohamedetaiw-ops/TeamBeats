/** Item analytics as the API returns them (spec 021 US1). Groups under the minimum size are already merged into "other". */
export type ItemTrendDay = { day: string; views: number; readers: number };

export type ItemGroupView = { group: string; names: Record<string, string>; audience: number; readers: number; acknowledged: number | null; reach: number };

export type ItemAnalyticsView = {
  source: string;
  itemId: string;
  titles: Record<string, string>;
  liveFrom: string | null;
  audience: number;
  readers: number;
  reach: number;
  acknowledgementRequired: boolean;
  acknowledged: number | null;
  acknowledgedShare: number | null;
  reactions: number;
  comments: number;
  minGroupSize: number;
  trend: ItemTrendDay[];
  groups: ItemGroupView[];
};

export type AnalyticsSource = "content" | "policies";

export const itemAnalyticsApi = (source: AnalyticsSource, id: string) => `/api/v1/analytics/items/${source}/${encodeURIComponent(id)}`;

export const otherGroup = "other";
export const noUnitGroup = "none";
