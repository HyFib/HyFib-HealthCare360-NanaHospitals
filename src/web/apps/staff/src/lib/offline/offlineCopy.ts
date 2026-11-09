import { create } from 'zustand';
import type { DowntimePack } from '@/modules/downtime/continuityApi';
import {
  deriveOfflineKey,
  forgetKeyForTab,
  keyFromTab,
  rememberKeyForTab,
  seal,
  unseal,
  type VaultBackend,
} from './vault';

/** A worklist as the offline copy keeps it: its title and the rows the user last saw. */
export interface OfflineWorklist {
  slot: string;
  title: string;
  rows: Record<string, unknown>[];
}

/**
 * The worklists kept for reading offline, each taken only when the user may open it (a 403 or 404 is skipped). The
 * pack holds the ward's census, medication sheets, pending orders and OT list.
 */
export const OFFLINE_WORKLISTS: readonly { slot: string; title: string; path: string }[] = [
  { slot: 'worklist:emr', title: 'OPD worklist', path: '/api/v1/emr/worklist' },
  { slot: 'worklist:lab', title: 'Laboratory worklist', path: '/api/v1/lab/worklist' },
  { slot: 'worklist:radiology', title: 'Radiology reporting worklist', path: '/api/v1/radiology/reporting-worklist' },
];

export const PACK_SLOT = 'pack';

/** How often the copy is taken again while HC360 can be reached. */
export const REFRESH_MS = 15 * 60 * 1000;

interface OfflineCopyState {
  /** When the copy in hand was taken (ms), or null when there is none. */
  takenAt: number | null;
  pack: DowntimePack | null;
  worklists: OfflineWorklist[];
}

export const useOfflineCopy = create<OfflineCopyState>(() => ({ takenAt: null, pack: null, worklists: [] }));

export interface OfflineCopyDeps {
  backend: VaultBackend | null;
  /** Reads a JSON resource from the server; throws when it cannot (refused, not found or unreachable). */
  fetchJson: (path: string) => Promise<unknown>;
  now?: () => number;
}

let key: CryptoKey | null = null;
let owner: string | null = null;

const aad = (userId: string, slot: string) => `${userId}:${slot}`;

/** The user's key has come: derive the offline key and keep it for this tab. */
export async function openOfflineCopy(userId: string, serverKey: string): Promise<void> {
  try {
    key = await deriveOfflineKey(serverKey, userId);
    owner = userId;
    await rememberKeyForTab(key);
  } catch {
    // No WebCrypto: no copy is kept.
    key = null;
    owner = null;
  }
}

/**
 * Takes the copy now: the latest pack and the worklists the user may open, sealed and kept. A slot the server cannot
 * give keeps what was kept before. Answers whether anything was taken.
 */
export async function takeOfflineCopy(deps: OfflineCopyDeps): Promise<boolean> {
  if (!key || !owner) {
    return false;
  }
  const at = (deps.now ?? Date.now)();
  const sealedWith = key;
  const userId = owner;
  let taken = false;
  let pack: DowntimePack | null = useOfflineCopy.getState().pack;
  try {
    pack = (await deps.fetchJson('/api/v1/downtime/pack/latest')) as DowntimePack;
    await deps.backend?.put(PACK_SLOT, await seal(sealedWith, aad(userId, PACK_SLOT), pack, at));
    taken = true;
  } catch {
    // No pack yet, no permission, or unreachable: keep the one held.
  }
  const worklists: OfflineWorklist[] = [];
  for (const list of OFFLINE_WORKLISTS) {
    try {
      const data = await deps.fetchJson(list.path);
      const rows = rowsOf(data);
      const copy: OfflineWorklist = { slot: list.slot, title: list.title, rows };
      await deps.backend?.put(list.slot, await seal(sealedWith, aad(userId, list.slot), copy, at));
      worklists.push(copy);
      taken = true;
    } catch {
      const held = useOfflineCopy.getState().worklists.find((w) => w.slot === list.slot);
      if (held) {
        worklists.push(held);
      }
    }
  }
  if (taken && owner === userId) {
    useOfflineCopy.setState({ takenAt: at, pack, worklists });
  }
  return taken;
}

/**
 * Opens the kept copy when the one in memory is gone (the tab was reloaded while HC360 is down): with the key kept for
 * this tab. Answers whether a copy is now in hand.
 */
export async function readKeptCopy(userId: string, backend: VaultBackend | null): Promise<boolean> {
  if (useOfflineCopy.getState().takenAt !== null) {
    return true;
  }
  const sealedWith = key ?? (await keyFromTab());
  if (!sealedWith || !backend) {
    return false;
  }
  const packRecord = await backend.get(PACK_SLOT);
  const pack = await unseal<DowntimePack>(sealedWith, aad(userId, PACK_SLOT), packRecord);
  const worklists: OfflineWorklist[] = [];
  let takenAt = pack ? (packRecord?.at ?? null) : null;
  for (const list of OFFLINE_WORKLISTS) {
    const record = await backend.get(list.slot);
    const copy = await unseal<OfflineWorklist>(sealedWith, aad(userId, list.slot), record);
    if (copy) {
      worklists.push(copy);
      takenAt = Math.max(takenAt ?? 0, record?.at ?? 0);
    }
  }
  if (takenAt === null) {
    return false;
  }
  useOfflineCopy.setState({ takenAt, pack, worklists });
  return true;
}

/** Sign-out: the copy is wiped from memory, from the computer and from the tab, and the cached app shell with it. */
export async function clearOfflineCopy(backend: VaultBackend | null): Promise<void> {
  key = null;
  owner = null;
  forgetKeyForTab();
  useOfflineCopy.setState({ takenAt: null, pack: null, worklists: [] });
  try {
    await backend?.clear();
  } catch {
    // Storage unavailable: nothing was kept.
  }
  try {
    if (typeof caches !== 'undefined') {
      for (const name of await caches.keys()) {
        if (name.startsWith('hc360-')) {
          await caches.delete(name);
        }
      }
    }
  } catch {
    // No Cache Storage.
  }
}

/** "12 minutes", "3 hours", "2 days": how old the copy is. */
export function ageText(takenAt: number, now: number): string {
  const minutes = Math.max(0, Math.floor((now - takenAt) / 60_000));
  if (minutes < 60) {
    return `${minutes} ${minutes === 1 ? 'minute' : 'minutes'}`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 48) {
    return `${hours} ${hours === 1 ? 'hour' : 'hours'}`;
  }
  return `${Math.floor(hours / 24)} days`;
}

/** The rows of a worklist answer: the array itself, or the `items` of a page. */
export function rowsOf(data: unknown): Record<string, unknown>[] {
  const list = Array.isArray(data)
    ? data
    : data && typeof data === 'object' && Array.isArray((data as { items?: unknown }).items)
      ? (data as { items: unknown[] }).items
      : [];
  return list.filter((r): r is Record<string, unknown> => !!r && typeof r === 'object').slice(0, 500);
}

/** The columns worth showing of a worklist row: its plain text, number and date fields, not ids or versions. */
export function columnsOf(rows: Record<string, unknown>[]): string[] {
  const columns: string[] = [];
  for (const row of rows.slice(0, 20)) {
    for (const [name, value] of Object.entries(row)) {
      const plain = typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean';
      if (plain && !/(^id$|Id$|version|^is[A-Z])/.test(name) && !columns.includes(name)) {
        columns.push(name);
      }
    }
  }
  return columns.slice(0, 8);
}

/** "patientName" → "Patient name". */
export function columnTitle(name: string): string {
  const words = name
    .replace(/([A-Z])/g, ' $1')
    .toLowerCase()
    .trim();
  return words.charAt(0).toUpperCase() + words.slice(1);
}
