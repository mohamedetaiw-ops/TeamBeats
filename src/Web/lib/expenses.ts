import type { PersonRef } from "@/lib/leave";

/** Expense claims (spec 022 US8, FR-023, FR-025): the shapes the API returns. Rules and limits live in the API. */
export type ExpenseStatus = "pending" | "approved" | "handed_off" | "paid" | "handoff_failed" | "rejected" | "withdrawn";

export type ExpenseKind = "amount" | "per_diem" | "mileage";

export type ExpenseCategoryView = { key: string; names: Record<string, string>; kind: ExpenseKind; receipt: boolean; rate: number | null; limit: number | null };

export type ExpenseForm = { currency: string | null; windowDays: number; categories: ExpenseCategoryView[]; costCentres: string[]; receiptReading: boolean };

/** What AI receipt reading suggested (spec 022 FR-024); the person confirms before any value is used. */
export type ReceiptSuggestion = { merchant: string | null; date: string | null; amount: number | null; currency: string | null; tax: number | null };

export type ReceiptReading = { outcome: "suggested" | "nothing_read" | "policy_blocked" | "unavailable"; suggestion: ReceiptSuggestion | null };

export type ExpenseLine = {
  n: number;
  category: string;
  kind: ExpenseKind;
  date: string;
  merchant: string | null;
  description: string | null;
  amount: number;
  currency: string;
  tax: number | null;
  days: number | null;
  km: number | null;
  rate: number;
  baseAmount: number;
  limit: number | null;
  overLimit: boolean;
  justification: string | null;
  receiptId: string | null;
  receiptName: string | null;
  duplicate: boolean;
};

export type ExpenseClaim = {
  id: string;
  reference: string;
  person: PersonRef;
  title: string;
  costCentre: string | null;
  project: string | null;
  currency: string;
  total: number;
  duplicate: boolean;
  overLimit: boolean;
  status: ExpenseStatus;
  financeState: string | null;
  externalReference: string | null;
  paymentStatus: string | null;
  createdAt: string;
  decidedAt: string | null;
  lines: ExpenseLine[];
  rights: { withdraw: boolean; decide: boolean };
};

export type ExpenseCategory = { key: string; names: Record<string, string>; kind: ExpenseKind; receipt: boolean; rate?: number | null; limits?: { grade: string | null; amount: number }[] | null };

export type ExpensePolicy = {
  currency: string | null;
  windowDays: number;
  categories: ExpenseCategory[];
  tiers: { above: number; approvers: string[] }[];
  costCentres: string[];
};

export type ExpensePolicyView = { policy: ExpensePolicy; version: number; canChange: boolean; eTag: string };

export type ExpenseRate = { currency: string; date: string; rate: number };

export const expenseTone: Record<ExpenseStatus, "neutral" | "info" | "success" | "danger" | "warning"> = {
  pending: "info",
  approved: "warning",
  handed_off: "info",
  paid: "success",
  handoff_failed: "danger",
  rejected: "danger",
  withdrawn: "neutral",
};

/** The category name in the page language, else English, else its key. */
export function categoryName(names: Record<string, string> | undefined, locale: string, key: string): string {
  return names?.[locale] ?? names?.en ?? key;
}
