"use client";

import type { ReactNode } from "react";
import { useRouter } from "next/navigation";

export function SignOutButton({ label, className = "button secondary", icon }: { label: string; className?: string; icon?: ReactNode }) {
  const router = useRouter();
  async function signOut() {
    await fetch("/api/v1/auth/signout", { method: "POST" });
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
