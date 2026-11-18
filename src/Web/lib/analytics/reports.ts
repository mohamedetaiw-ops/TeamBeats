import type { MetricFilters } from "./types";

/** Spec 021 T016: scheduled reports and exports. The API decides what may be exported or scheduled and runs each report. */
export type ReportDashboard = "overview" | "campaign" | "service" | "compliance" | "scope_unit" | "scope_location" | "experience" | "sentiment";

export type ReportDefinition = {
  dashboard: ReportDashboard;
  target?: string | null;
  metric?: string | null;
  filters?: MetricFilters | null;
  groupBy?: string | null;
};

export type ReportStatus = "queued" | "ready" | "skipped" | "failed";

export type ReportSummary = {
  id: string;
  name: string;
  dashboard: string;
  scheduleId: string | null;
  mine: boolean;
  format: "csv" | "xlsx" | null;
  status: ReportStatus;
  periodFrom: string | null;
  periodTo: string | null;
  createdAt: string;
  doneAt: string | null;
};

export type ReportSchedule = {
  id: string;
  name: string;
  definition: ReportDefinition;
  cadence: "weekly" | "monthly";
  recipientUnitId: string | null;
  recipientUnitNames: Record<string, string> | null;
  nextRunAt: string;
  lastRunAt: string | null;
  createdAt: string;
};

export type ReportCell = string | number | boolean | null;
export type ReportTable = { key: string; columns: string[]; rows: ReportCell[][] };

export type ReportView = { report: ReportSummary; definition: ReportDefinition; canDownload: boolean; tables: ReportTable[] };
export type ReportList = { canExport: boolean; canSchedule: boolean; reports: ReportSummary[]; schedules: ReportSchedule[] };
export type ReportOptions = { canExport: boolean; canSchedule: boolean; units: { id: string; names: Record<string, string> }[] };

export const reportsApi = "/api/v1/analytics/reports";
export const exportsApi = "/api/v1/analytics/exports";
export const schedulesApi = "/api/v1/analytics/schedules";
export const reportHref = (id: string) => `/analytics/reports/${encodeURIComponent(id)}`;
export const reportFileHref = (id: string, format: "csv" | "xlsx") => `${reportsApi}/${encodeURIComponent(id)}/file?format=${format}`;

/** The one event between a dashboard and its report actions: the definition the dashboard shows now. */
export const reportDefinitionEvent = "tb:analytics-report-definition";

export function announceReportDefinition(definition: ReportDefinition): void {
  window.dispatchEvent(new CustomEvent<ReportDefinition>(reportDefinitionEvent, { detail: definition }));
}
