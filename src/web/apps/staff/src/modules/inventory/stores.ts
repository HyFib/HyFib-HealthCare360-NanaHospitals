import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { inventoryQueries, type StoreDto } from './api';
import type { StoreType } from './labels';
import { STORE_READERS } from './permissions';

/** Which counter or desk the workstation is used for; each remembers its own store. */
export type WorkingStoreScope = 'pharmacy-op' | 'pharmacy-ip' | 'inventory' | 'purchase';

const PREFIX = 'hc360:working-store:';

function readSaved(scope: WorkingStoreScope): string | null {
  try {
    return localStorage.getItem(PREFIX + scope);
  } catch {
    return null;
  }
}

function writeSaved(scope: WorkingStoreScope, id: string | null): void {
  try {
    if (id) {
      localStorage.setItem(PREFIX + scope, id);
    } else {
      localStorage.removeItem(PREFIX + scope);
    }
  } catch {
    // The remembered store is a convenience only.
  }
}

/** The store to start with: the first preferred code that exists (e.g. OPPH at the OP counter), else the first store. */
export function defaultStore(stores: readonly StoreDto[], preferCodes: readonly string[] = []): StoreDto | null {
  for (const code of preferCodes) {
    const match = stores.find((s) => s.code.toUpperCase() === code.toUpperCase());
    if (match) {
      return match;
    }
  }
  return stores[0] ?? null;
}

export const PREFERRED_STORES: Record<WorkingStoreScope, readonly string[]> = {
  'pharmacy-op': ['OPPH'],
  'pharmacy-ip': ['IPPH'],
  inventory: ['CS'],
  purchase: ['CS'],
};

/**
 * The store this workstation works in, remembered per workstation (a counter always sells from the same
 * pharmacy). The server re-checks every store on every call.
 */
export function useWorkingStore(scope: WorkingStoreScope, options: { type?: StoreType } = {}) {
  const canRead = usePermission(STORE_READERS);
  const query = useQuery({ ...inventoryQueries.stores(options.type), enabled: canRead });
  const [chosen, setChosen] = useState<string | null>(() => readSaved(scope));
  const stores = query.data ?? [];
  const store = stores.find((s) => s.id === chosen) ?? defaultStore(stores, PREFERRED_STORES[scope]);

  const setStoreId = (id: string | null) => {
    setChosen(id);
    writeSaved(scope, id);
  };

  return {
    stores,
    store,
    storeId: store?.id ?? null,
    setStoreId,
    loading: canRead && query.isLoading,
    error: query.error,
    /** The user cannot list stores (no inventory or store permission). */
    noAccess: !canRead,
    refetch: () => void query.refetch(),
  };
}

export type WorkingStore = ReturnType<typeof useWorkingStore>;
