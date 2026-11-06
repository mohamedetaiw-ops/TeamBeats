import type { ContrastFinding } from "@/lib/brand/types";

export type ContrastLabels = {
  title: string;
  token: string;
  against: string;
  mode: string;
  ratio: string;
  required: string;
  result: string;
  blocking: string;
  warning: string;
  empty: string;
  modeName: (mode: string) => string;
};

/** Contrast and size checks from the API, with ratios in text so the table reads without color (WCAG 1.4.1). */
export function ContrastTable({ findings, labels, locale }: { findings: ContrastFinding[]; labels: ContrastLabels; locale: string }) {
  const number = new Intl.NumberFormat(locale, { maximumFractionDigits: 2 });
  return (
    <section className="brand-section" aria-labelledby="brand-contrast-title">
      <h2 id="brand-contrast-title">{labels.title}</h2>
      {findings.length === 0 ? (
        <p className="notice" data-testid="contrast-empty">
          {labels.empty}
        </p>
      ) : (
        <div className="brand-table-wrap">
          <table className="brand-table" data-testid="contrast-table">
            <thead>
              <tr>
                <th scope="col">{labels.token}</th>
                <th scope="col">{labels.against}</th>
                <th scope="col">{labels.mode}</th>
                <th scope="col">{labels.ratio}</th>
                <th scope="col">{labels.required}</th>
                <th scope="col">{labels.result}</th>
              </tr>
            </thead>
            <tbody>
              {findings.map((f) => (
                <tr key={`${f.token}-${f.against}-${f.mode}`} data-severity={f.severity}>
                  <td>
                    <code dir="ltr">{f.token}</code>
                  </td>
                  <td>
                    <code dir="ltr">{f.against}</code>
                  </td>
                  <td>{labels.modeName(f.mode)}</td>
                  <td>{number.format(f.ratio)}</td>
                  <td>{number.format(f.required)}</td>
                  <td>{f.severity === "blocking" ? labels.blocking : labels.warning}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
