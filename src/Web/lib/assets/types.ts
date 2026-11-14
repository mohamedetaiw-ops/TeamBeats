/** Shapes of the spec 011 R2 brand asset portal API, as the browser reads them. */
import type { Names } from "@/lib/documents/types";

/** What the caller may do in the portal. */
export type AssetPortal = { canManage: boolean };

export type AssetCollection = {
  id: string;
  names: Names;
  descriptions: Names;
  restrictedTo: string | null;
  status: "active" | "retired";
  version: number;
  canRead: boolean;
  canManage: boolean;
  accessRequested: boolean;
  assetCount: number;
};

export type AssetRendition = { key: string; contentType: string; sizeBytes: number | null; ready: boolean };

export type Asset = {
  id: string;
  collectionId: string;
  names: Names;
  usageNotes: Names;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  rightsExpireAt: string | null;
  expired: boolean;
  status: "active" | "retired";
  version: number;
  createdAt: string;
  renditions: AssetRendition[];
};

export type AssetAccessRequest = {
  id: string;
  collectionId: string;
  collectionNames: Names;
  userId: string;
  userName: string;
  reason: string;
  status: "pending" | "approved" | "declined";
  createdAt: string;
  decidedAt: string | null;
};

export type AssetDownloadRow = { assetId: string; assetNames: Names; rendition: string; userId: string; userName: string; at: string };
export type AssetDownloadCount = { assetId: string; assetNames: Names; rendition: string; downloads: number; people: number };
export type AssetDownloadReport = { downloads: AssetDownloadRow[]; totals: AssetDownloadCount[] };

export const assetsHref = "/assets";
export const assetRequestsHref = "/assets/requests";
export const assetsApi = "/api/v1/assets";

/** The rendition keys the API makes, in the order the portal offers them. */
export const renditionOrder = ["original", "png_256", "png_512", "png_1024", "svg", "pdf"] as const;
