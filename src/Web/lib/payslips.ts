/** My payslips (spec 019 US6), read live from payroll; never cached or stored in the browser. */
export type Payslip = { period: string; gross: number | null; deductions: number | null; net: number; currency: string | null };

export type PayslipList = { state: "ok" | "not_matched" | "unavailable"; payslips: Payslip[]; hasPdf: boolean };
