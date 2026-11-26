"use client";

import { useEffect, useRef, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { PlacementProps } from "@/lib/experience/contract";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import "@/components/experience/sandbox.css";

type Runtime = { id: string; version: number; names: Record<string, string>; context: string[]; events: string[]; height: number };

/** Theme tokens a tenant component may style itself with (spec 033 public contract), read from the page. */
const TOKENS = [
  "--tb-color-text-primary",
  "--tb-color-text-secondary",
  "--tb-color-surface-raised",
  "--tb-color-surface-base",
  "--tb-color-border-default",
  "--tb-color-accent-base",
  "--tb-font-family-body",
  "--tb-radius-md",
] as const;

/**
 * A component the tenant built itself (spec 032 R3 FR-031, ADR-0012 point 6). Its document runs in a frame with an
 * opaque origin (the server's policy and `sandbox="allow-scripts"`), so it has no session, storage or access to this
 * page. The page talks to it only by messages: it sends the context keys the component declared, the theme tokens and
 * the direction; it accepts only a height from that frame. Anything else from the frame is ignored.
 */
export default function TenantComponent({ props, context }: PlacementProps) {
  const t = useTranslations("sandbox");
  const locale = useLocale();
  const frame = useRef<HTMLIFrameElement>(null);
  const id = typeof props.component === "string" ? props.component : "";
  const [runtime, setRuntime] = useState<{ id: string; value: Runtime | "unavailable" } | null>(null);
  const [height, setHeight] = useState<number | null>(null);
  const current = runtime && runtime.id === id ? runtime.value : null;

  useEffect(() => {
    if (!id) return;
    let live = true;
    fetch(`/api/v1/experience/sandbox/${encodeURIComponent(id)}`)
      .then(async (r) => (r.ok ? ((await r.json()) as Runtime) : ("unavailable" as const)))
      .catch(() => "unavailable" as const)
      .then((value) => {
        if (live) setRuntime({ id, value });
      });
    return () => {
      live = false;
    };
  }, [id]);

  useEffect(() => {
    if (!current || current === "unavailable") return;
    function onMessage(event: MessageEvent) {
      // Only the component's own frame is heard, and only its height.
      if (event.source !== frame.current?.contentWindow) return;
      const data = event.data as { type?: unknown; height?: unknown } | null;
      if (data && data.type === "tb.height" && typeof data.height === "number" && Number.isFinite(data.height)) {
        setHeight(Math.min(1200, Math.max(80, Math.round(data.height))));
      }
    }
    window.addEventListener("message", onMessage);
    return () => window.removeEventListener("message", onMessage);
  }, [current]);

  function send() {
    if (!current || current === "unavailable" || !frame.current?.contentWindow) return;
    const shared = Object.fromEntries(current.context.filter((k) => k in context).map((k) => [k, context[k]]));
    const style = getComputedStyle(document.documentElement);
    const tokens = Object.fromEntries(TOKENS.map((k) => [k, style.getPropertyValue(k).trim()]));
    // The frame has an opaque origin, so the message can only be addressed to any origin; it carries nothing private.
    frame.current.contentWindow.postMessage({ type: "tb.context", context: shared, tokens }, "*");
  }

  if (!id || current === "unavailable") {
    return (
      <Widget title={t("title")}>
        <StateView kind="empty" title={t("unavailable")} message={t("unavailableHint")} />
      </Widget>
    );
  }
  if (!current) {
    return <Widget title={t("title")} busy>{null}</Widget>;
  }
  const name = current.names[locale] ?? current.names.en ?? id;
  return (
    <Widget title={name}>
      <iframe
        ref={frame}
        className="sandbox-frame"
        title={t("frameTitle", { name })}
        src={`/api/v1/experience/sandbox/${encodeURIComponent(current.id)}/${current.version}`}
        sandbox="allow-scripts"
        referrerPolicy="no-referrer"
        loading="lazy"
        style={{ blockSize: `${height ?? current.height}px` }}
        onLoad={send}
        data-testid="sandbox-frame"
      />
    </Widget>
  );
}
