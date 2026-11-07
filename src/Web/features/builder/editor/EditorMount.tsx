"use client";

import { useSyncExternalStore, type ComponentProps } from "react";
import { useTranslations } from "next-intl";
import { StateView } from "@/components/ui/StateView";
import { PageEditor } from "./PageEditor";

const never = () => () => {};

/** The editor reads unsaved edits kept in this browser, so it starts only once the page has hydrated. */
export function EditorMount(props: ComponentProps<typeof PageEditor>) {
  const t = useTranslations("builder.editor");
  const hydrated = useSyncExternalStore(never, () => true, () => false);
  if (!hydrated) return <StateView kind="loading" title={t("loading")} message="" minBlockSize="20rem" />;
  return <PageEditor {...props} />;
}
