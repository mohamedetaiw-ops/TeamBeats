/**
 * Core Web Vitals for a rendered page (spec 032 FR-058) from the browser's own performance entries, without a library:
 * LCP is the last largest-contentful-paint before input, CLS the largest session window of layout shifts without recent
 * input (gaps under 1 s, windows up to 5 s), and INP the longest interaction. Browsers that lack an entry type report
 * that value as missing.
 */
export type Vitals = { lcp: number | null; inp: number | null; cls: number | null };

type Shift = PerformanceEntry & { value: number; hadRecentInput: boolean };
type Interaction = PerformanceEntry & { interactionId?: number };

function observe(type: string, callback: (entries: PerformanceEntry[]) => void, options: Record<string, unknown> = {}): PerformanceObserver | null {
  if (typeof PerformanceObserver === "undefined" || !PerformanceObserver.supportedEntryTypes?.includes(type)) return null;
  const observer = new PerformanceObserver((list) => callback(list.getEntries()));
  observer.observe({ type, buffered: true, ...options } as PerformanceObserverInit);
  return observer;
}

/** Starts collecting; the returned function gives the values so far and stops. */
export function collectVitals(): () => Vitals {
  const vitals: Vitals = { lcp: null, inp: null, cls: null };
  let windowValue = 0;
  let windowStart = 0;
  let last = 0;
  const observers = [
    observe("largest-contentful-paint", (entries) => {
      const entry = entries.at(-1);
      if (entry) vitals.lcp = Math.round(entry.startTime);
    }),
    observe("layout-shift", (entries) => {
      for (const entry of entries as Shift[]) {
        if (entry.hadRecentInput) continue;
        if (entry.startTime - last > 1000 || entry.startTime - windowStart > 5000) {
          windowValue = 0;
          windowStart = entry.startTime;
        }
        windowValue += entry.value;
        last = entry.startTime;
        vitals.cls = Math.max(vitals.cls ?? 0, Number(windowValue.toFixed(4)));
      }
    }),
    observe(
      "event",
      (entries) => {
        for (const entry of entries as Interaction[]) {
          if (entry.interactionId) vitals.inp = Math.max(vitals.inp ?? 0, Math.round(entry.duration));
        }
      },
      { durationThreshold: 16 },
    ),
  ];
  // A page with no shifts at all scores 0 where the browser reports shifts.
  if (observers[1]) vitals.cls = 0;
  return () => {
    for (const o of observers) o?.disconnect();
    return vitals;
  };
}

/** Device class by viewport width, the segments FR-058 asks for. */
export function deviceClass(width: number): "mobile" | "tablet" | "desktop" {
  return width < 768 ? "mobile" : width < 1024 ? "tablet" : "desktop";
}
