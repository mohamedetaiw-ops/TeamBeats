"use client";

/**
 * Offline reading on this device (spec 030 US3, FR-009 to FR-012). While online the app fetches the person's offline
 * bundle and keeps it in IndexedDB, every item encrypted with AES-GCM under the session's key. The key is stored as a
 * non-extractable key, so no script can read it out, and it changes with every session. One person's cache is kept per
 * tenant host; signing out, an ended session or an expired cache deletes it.
 *
 * The offline page reads the same database with public/offline-reader.js, which works with no connection. Both follow
 * this layout: database "tb-offline" version 1, store "cache" holding one record under the key "current":
 * { owner, key, syncedAt, expiresAt, items: [{ id, kind, iv, data }], drafts?: { [name]: { iv, data, savedAt } } }, data
 * being the encrypted JSON of one item. A draft (spec 030 R3 MOB-025, a leave request filled with no connection) is
 * encrypted under the same key and goes with the cache: a new session's key cannot read it, and a wipe deletes it.
 */

const database = "tb-offline";
const store = "cache";
const record = "current";

/** One item as the API gives it (Content.Contracts OfflineItem). */
export type OfflineItem = {
  kind: "announcement" | "policy";
  id: string;
  version: number;
  locale: string;
  title: string;
  summary: string | null;
  body: unknown;
  link: string;
  updatedAt: string;
};

export type OfflineBundle = {
  owner: string;
  key: string;
  syncedAt: string;
  expiresAt: string;
  limits: { maxItems: number; maxBytes: number; days: number };
  items: OfflineItem[];
};

type StoredItem = { id: string; kind: string; iv: Uint8Array<ArrayBuffer>; data: ArrayBuffer };
type StoredDraft = { iv: Uint8Array<ArrayBuffer>; data: ArrayBuffer; savedAt: string };
type StoredCache = { owner: string; key: CryptoKey; syncedAt: string; expiresAt: string; items: StoredItem[]; drafts?: Record<string, StoredDraft> };

export function offlineSupported(): boolean {
  return typeof window !== "undefined" && "indexedDB" in window && !!window.crypto?.subtle;
}

function open(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(database, 1);
    request.onupgradeneeded = () => {
      if (!request.result.objectStoreNames.contains(store)) request.result.createObjectStore(store);
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function keyBytes(base64url: string): Uint8Array<ArrayBuffer> {
  const s = base64url.replace(/-/g, "+").replace(/_/g, "/");
  const raw = atob(s + "=".repeat((4 - (s.length % 4)) % 4));
  const bytes = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}

async function get(): Promise<StoredCache | null> {
  const db = await open();
  try {
    return await new Promise<StoredCache | null>((resolve, reject) => {
      const request = db.transaction(store, "readonly").objectStore(store).get(record);
      request.onsuccess = () => resolve((request.result as StoredCache | undefined) ?? null);
      request.onerror = () => reject(request.error);
    });
  } finally {
    db.close();
  }
}

async function put(value: StoredCache): Promise<void> {
  const db = await open();
  try {
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction(store, "readwrite");
      tx.objectStore(store).put(value, record);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
  } finally {
    db.close();
  }
}

/** Deletes everything kept for offline reading on this tenant host (FR-011). */
export function wipeOffline(): Promise<void> {
  if (!offlineSupported()) return Promise.resolve();
  return new Promise((resolve) => {
    const request = indexedDB.deleteDatabase(database);
    request.onsuccess = () => resolve();
    request.onerror = () => resolve();
    request.onblocked = () => resolve();
  });
}

export type SyncResult = "synced" | "wiped" | "skipped";

/**
 * Brings the cache up to date: the bundle replaces what was kept, so withdrawn items and items the person lost access
 * to are pruned (US3 scenario 3). Without a session, or without offline reading in the plan, the cache is deleted.
 */
export async function syncOffline(): Promise<SyncResult> {
  if (!offlineSupported() || !navigator.onLine) return "skipped";
  let response: Response;
  try {
    response = await fetch("/api/v1/me/offline", { cache: "no-store", headers: { accept: "application/json" } });
  } catch {
    return "skipped";
  }
  if (!response.ok) {
    // Read the answer to the end, so the request is complete and nothing stays open.
    await response.text().catch(() => "");
    if (response.status === 401 || response.status === 403 || response.status === 409) {
      await wipeOffline();
      return "wiped";
    }
    return "skipped";
  }
  const bundle = (await response.json()) as OfflineBundle;
  const key = await crypto.subtle.importKey("raw", keyBytes(bundle.key), { name: "AES-GCM" }, false, ["encrypt", "decrypt"]);
  const encoder = new TextEncoder();
  const items: StoredItem[] = [];
  let bytes = 0;
  for (const item of bundle.items.slice(0, bundle.limits.maxItems)) {
    const iv = crypto.getRandomValues(new Uint8Array(new ArrayBuffer(12)));
    const data = await crypto.subtle.encrypt({ name: "AES-GCM", iv }, key, encoder.encode(JSON.stringify(item)));
    if (bytes + data.byteLength > bundle.limits.maxBytes) continue;
    bytes += data.byteLength;
    items.push({ id: item.id, kind: item.kind, iv, data });
  }
  // Drafts stay with the same person's cache; under a new session's key they could not be read, so they go.
  const previous = await get().catch(() => null);
  const drafts = previous?.owner === bundle.owner ? previous.drafts : undefined;
  await put({ owner: bundle.owner, key, syncedAt: bundle.syncedAt, expiresAt: bundle.expiresAt, items, drafts });
  return "synced";
}

/**
 * Keeps a form filled with no connection on this device (spec 030 R3 MOB-025), encrypted under the cache key. Answers
 * false when there is no cache to keep it in, for example when offline reading is not in the plan.
 */
export async function saveDraft(name: string, value: unknown): Promise<boolean> {
  if (!offlineSupported()) return false;
  try {
    const cache = await get();
    if (!cache || new Date(cache.expiresAt).getTime() < Date.now()) return false;
    const iv = crypto.getRandomValues(new Uint8Array(new ArrayBuffer(12)));
    const data = await crypto.subtle.encrypt({ name: "AES-GCM", iv }, cache.key, new TextEncoder().encode(JSON.stringify(value)));
    await put({ ...cache, drafts: { ...cache.drafts, [name]: { iv, data, savedAt: new Date().toISOString() } } });
    return true;
  } catch {
    return false;
  }
}

/** The draft kept on this device, or null when there is none or it cannot be read under this session's key. */
export async function readDraft<T>(name: string): Promise<{ value: T; savedAt: string } | null> {
  if (!offlineSupported()) return null;
  try {
    const cache = await get();
    const draft = cache?.drafts?.[name];
    if (!cache || !draft) return null;
    const plain = await crypto.subtle.decrypt({ name: "AES-GCM", iv: draft.iv }, cache.key, draft.data);
    return { value: JSON.parse(new TextDecoder().decode(plain)) as T, savedAt: draft.savedAt };
  } catch {
    return null;
  }
}

export async function clearDraft(name: string): Promise<void> {
  if (!offlineSupported()) return;
  try {
    const cache = await get();
    if (!cache?.drafts?.[name]) return;
    const { [name]: _removed, ...rest } = cache.drafts;
    await put({ ...cache, drafts: rest });
  } catch {
    // A draft that cannot be removed is still sent only when the person confirms it.
  }
}
