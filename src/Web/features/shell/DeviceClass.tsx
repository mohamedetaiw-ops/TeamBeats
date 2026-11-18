"use client";

import { useEffect } from "react";

/** The device classes of spec 008 FR-032 by screen width: a phone below 640px, a tablet below 1024px, else a desktop. */
function deviceClass(width: number): "phone" | "tablet" | "desktop" {
  return width < 640 ? "phone" : width < 1024 ? "tablet" : "desktop";
}

/**
 * Keeps the class of this screen in the `tb_device` cookie, so the API shows the person's device-specific choices for
 * it (spec 008 FR-032). It only picks among the person's own choices and grants nothing. It renders nothing.
 */
export function DeviceClass() {
  useEffect(() => {
    const write = () => {
      const value = deviceClass(window.innerWidth);
      if (!document.cookie.split("; ").includes(`tb_device=${value}`)) {
        document.cookie = `tb_device=${value}; path=/; max-age=31536000; samesite=lax`;
      }
    };
    write();
    window.addEventListener("resize", write);
    return () => window.removeEventListener("resize", write);
  }, []);
  return null;
}
