import "server-only";
import { apiGet, apiPost, type ApiResult } from "@/lib/api";
import type { BrandCheck, BrandSummary, BrandView, TokenSchema } from "./types";

export const getTokenSchema = (): Promise<ApiResult<TokenSchema>> => apiGet<TokenSchema>("/api/v1/experience/tokens");

export const listBrands = (): Promise<ApiResult<BrandSummary[]>> => apiGet<BrandSummary[]>("/api/v1/experience/brands");

export const getBrand = (id: string): Promise<ApiResult<BrandView>> => apiGet<BrandView>(`/api/v1/experience/brands/${encodeURIComponent(id)}`);

/** Findings and preview properties for a brand as saved, so the page opens with its preview already drawn. */
export const checkBrand = (brand: BrandView): Promise<ApiResult<BrandCheck>> =>
  apiPost<BrandCheck>(`/api/v1/experience/brands/${encodeURIComponent(brand.id)}/check`, {
    name: brand.name,
    tokens: brand.tokens,
    strictContrast: brand.strictContrast,
  });
