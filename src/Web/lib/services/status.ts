import type { RequestStatus } from "./types";

/**
 * The badge tone of a request status (spec 036 DLS-046 status badges). Tone only colours the badge; the words always
 * say the status, and the status itself comes from the API.
 */
export function requestTone(status: RequestStatus): "accent" | "info" | "success" | "warning" | "danger" | undefined {
  switch (status) {
    case "submitted":
      return "info";
    case "in_approval":
      return "accent";
    case "returned":
      return "warning";
    case "approved":
    case "completed":
      return "success";
    case "rejected":
      return "danger";
    default:
      return undefined;
  }
}
