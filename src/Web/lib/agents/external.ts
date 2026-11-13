/** An action an external AI client asked to do for the person, as the API shows it (spec 035 FR-022). */
export type ExternalAction = {
  id: string;
  appName: string;
  clientId: string;
  tool: string;
  sideEffect: string;
  parameters: Record<string, unknown>;
  hash: string;
  status: "pending" | "confirmed" | "rejected" | "expired" | "failed";
  reason: string | null;
  createdAt: string;
  expiresAt: string;
  decidedAt: string | null;
  result: unknown;
};
