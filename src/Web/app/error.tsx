"use client";

import { useEffect } from "react";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { areaOf, kindOf, reportUiError } from "@/lib/analytics/errors";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";

/**
 * The app's error boundary (spec 021 US9 scenario 6): the shared error state with a retry, and a report with the
 * release, the area and the error's digest as the correlation ID. The message itself is never sent.
 */
export default function AppError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const t = useTranslations("errors.boundary");
  const pathname = usePathname();
  useEffect(() => {
    reportUiError({ kind: kindOf(error, "boundary"), area: areaOf(pathname ?? "/"), correlationId: error.digest ?? null });
  }, [error, pathname]);
  return (
    <main id="main" className="page">
      <StateView
        kind="error"
        action={
          <Button variant="primary" onClick={() => reset()}>
            {t("retry")}
          </Button>
        }
      />
    </main>
  );
}
