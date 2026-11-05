"use client";

import { useRouter } from "next/navigation";

export function SignOutButton({ label }: { label: string }) {
  const router = useRouter();
  async function signOut() {
    await fetch("/api/v1/auth/signout", { method: "POST" });
    router.push("/signin");
    router.refresh();
  }
  return (
    <button type="button" className="button secondary" onClick={() => void signOut()}>
      {label}
    </button>
  );
}
