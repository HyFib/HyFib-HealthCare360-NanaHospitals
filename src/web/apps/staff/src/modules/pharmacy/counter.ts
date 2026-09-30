import { useRealtimeEvent, useSession } from '@hc360/core';
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { facilityQuery, inventoryQueries, type BatchStock, type ItemDetail } from '@/modules/inventory/api';
import { CATALOG_READERS } from '@/modules/inventory/permissions';
import { pharmacyKeys, pharmacyQueries } from './api';
import { addressLine, type Letterhead } from './print';

/** Counter preferences kept on this workstation. */
export interface CounterPrefs {
  /** Print the invoice on the receipt printer as soon as a sale or dispense is saved. */
  autoPrintReceipt: boolean;
  /** Print medicine labels automatically after dispensing a prescription. */
  autoPrintLabels: boolean;
}

const PREFS_KEY = 'hc360:pharmacy-counter';
const DEFAULT_PREFS: CounterPrefs = { autoPrintReceipt: true, autoPrintLabels: false };

function readPrefs(): CounterPrefs {
  try {
    return { ...DEFAULT_PREFS, ...(JSON.parse(localStorage.getItem(PREFS_KEY) ?? '{}') as Partial<CounterPrefs>) };
  } catch {
    return DEFAULT_PREFS;
  }
}

export function useCounterPrefs(): [CounterPrefs, (patch: Partial<CounterPrefs>) => void] {
  const [prefs, setPrefs] = useState<CounterPrefs>(readPrefs);
  const update = (patch: Partial<CounterPrefs>) => {
    const next = { ...prefs, ...patch };
    setPrefs(next);
    try {
      localStorage.setItem(PREFS_KEY, JSON.stringify(next));
    } catch {
      // Preferences are a convenience only.
    }
  };
  return [prefs, update];
}

/** Hospital name, address, phone and GSTIN for prints (GSTIN and address need the masters permission). */
export function useLetterhead(): Letterhead {
  const user = useSession((s) => s.user);
  const canReadMasters = usePermission('masters.read');
  const facility = useQuery({
    ...facilityQuery(user?.facilityId),
    enabled: Boolean(user?.facilityId) && canReadMasters,
  });
  return {
    name: facility.data?.legalName || facility.data?.name || user?.facilityName || 'Nana Hospitals',
    address: addressLine(facility.data?.address),
    phone: facility.data?.phone ?? null,
    gstin: facility.data?.gstin ?? null,
  };
}

/**
 * Item-master details (GST rate, HSN) for items on a bill. Only catalogue readers can see the master; without it
 * the bill preview still shows the exact payable amount and leaves the CGST/SGST breakup to the invoice.
 */
export function useItemMaster(itemIds: readonly string[]): {
  canRead: boolean;
  items: ReadonlyMap<string, ItemDetail>;
} {
  const canRead = usePermission(CATALOG_READERS);
  const results = useQueries({
    queries: itemIds.map((id) => ({ ...inventoryQueries.item(id), enabled: canRead, retry: false })),
  });
  const items = new Map<string, ItemDetail>();
  for (let index = 0; index < results.length; index++) {
    const id = itemIds[index];
    const data = results[index]?.data;
    if (id && data) {
      items.set(id, data);
    }
  }
  return { canRead, items };
}

/** Batches (FEFO order) of each item in the pharmacy store; `undefined` while loading. */
export function usePharmacyBatches(storeId: string | null, itemIds: readonly string[]) {
  const results = useQueries({
    queries: itemIds.map((id) => ({ ...pharmacyQueries.batches(storeId ?? '', id), enabled: Boolean(storeId) })),
  });
  const batches = new Map<string, BatchStock[] | undefined>();
  for (let index = 0; index < results.length; index++) {
    const id = itemIds[index];
    if (id) {
      batches.set(id, results[index]?.data);
    }
  }
  const error = results.find((r) => r.error)?.error ?? null;
  return {
    batches,
    error,
    refetch: () => {
      for (const result of results) {
        void result.refetch();
      }
    },
  };
}

/** Facility setting pharmacy.discount.max_percent is not exposed to clients; the server enforces it (default 10%). */
export const DEFAULT_MAX_DISCOUNT_PERCENT = 10;

/** Keeps pharmacy numbers live: any queue, ward indent or refill change refreshes them. */
export function usePharmacyLive() {
  const facilityId = useSession((s) => s.user?.facilityId);
  const queryClient = useQueryClient();
  const topic = facilityId ? `pharmacy:${facilityId}` : null;
  useRealtimeEvent(topic, null, () => {
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.queue });
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.dashboard });
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.ward });
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.refills });
  });
}
