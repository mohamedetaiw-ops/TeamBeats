"use client";

import type { ReactNode } from "react";
import { useRouter } from "next/navigation";
import { forgetDevicePush } from "@/lib/push";

export function SignOutButton({ label, className = "button secondary", icon }: { label: string; className?: string; icon?: ReactNode }) {
  const router = useRouter();
  async function signOut() {
    await fetch("/api/v1/auth/signout", { method: "POST" });
    await forgetDevicePush();
    router.push("/signin");
    router.refresh();
  }
  return (
    <button type="button" className={className} onClick={() => void signOut()}>
      {icon}
      {label}
    </button>
  );
}
