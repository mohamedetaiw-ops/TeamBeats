import type { BadgeTone } from "@/components/ui/Badge";

/** The badge tone of an Agent Security Center severity (spec 027 FR-023). */
export const severityTone = (severity: string): BadgeTone =>
  severity === "critical" ? "danger" : severity === "high" ? "warning" : severity === "medium" ? "info" : "neutral";
