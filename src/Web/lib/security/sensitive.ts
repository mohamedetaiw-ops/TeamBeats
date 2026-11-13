/** A kind of sensitive data found in one place (spec 026 US6). Never the value itself. */
export type SensitiveFinding = { type: string; name: string | null; location: string; count: number; blocking: boolean };

/** A publish or upload stopped by sensitive data detection: a warning to confirm, or a block. */
export type SensitiveProblem = { kind: "found" | "blocked"; findings: SensitiveFinding[]; message: string };

/**
 * Reads a refused response as a sensitive data problem, or null when it is some other problem. Pass a clone when the
 * body may still be needed.
 */
export async function readSensitive(response: Response): Promise<SensitiveProblem | null> {
  if (response.status !== 409) return null;
  try {
    const problem = (await response.json()) as { detailKey?: string; detail?: string; findings?: SensitiveFinding[] };
    const kind =
      problem.detailKey === "conflict.security.sensitive_data_found" ? "found" : problem.detailKey === "conflict.security.sensitive_data_blocked" ? "blocked" : null;
    return kind ? { kind, findings: problem.findings ?? [], message: problem.detail ?? "" } : null;
  } catch {
    return null;
  }
}
