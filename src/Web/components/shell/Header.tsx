import type { Me } from "@/lib/api";
import type { Crumb } from "@/lib/shell/types";
import { Shell } from "@/features/shell/Shell";

/** The page header is the spec 008 shell; kept as an entry point so every page renders the same frame. */
export function Header({ me, breadcrumbs }: { me: Me; breadcrumbs?: Crumb[] }) {
  return <Shell me={me} breadcrumbs={breadcrumbs} />;
}
