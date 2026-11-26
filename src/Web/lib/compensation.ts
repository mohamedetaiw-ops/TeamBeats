/** My compensation (spec 019 R3 US10), read live from HR; never cached or stored in the browser. */
export type CompensationComponent = { code: string; name: string | null; amount: number; currency: string | null; frequency: string | null; startDate: string | null };

export type CompensationStatement = {
  components: CompensationComponent[];
  total: number | null;
  currency: string | null;
  frequency: string | null;
  effectiveDate: string | null;
};

export type CompensationRead = { state: "ok" | "not_matched" | "unavailable"; statement: CompensationStatement | null };
