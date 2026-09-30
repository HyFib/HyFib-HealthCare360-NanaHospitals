import { dec, min, toNumber, ZERO, type Dec } from './decimal';

/**
 * First-expiry-first-out batch suggestion for the counters and stores — the client mirror of the server's
 * FefoAllocator, used to show which packs to pick before anything is saved:
 * - blocked, empty and expired batches are never suggested (the expiry day itself is still usable);
 * - a minimum shelf life (e.g. the course length, at most 30 days, when dispensing) skips batches that would
 *   expire before the course ends;
 * - earliest expiry first, batches without expiry last; ties keep the server's order (earliest receipt first);
 * - a batch the user picked or scanned is used first.
 *
 * The UI then sends the chosen batch ids explicitly, so the invoice records exactly the packs handed over.
 */
export interface FefoBatch {
  batchId: string;
  batchNumber: string;
  expiryDate: string | null;
  available: number;
  isExpired: boolean;
  isBlocked: boolean;
  blockReason?: string | null;
  /** Days from today (IST) to expiry, as computed by the server; null when the item does not expire. */
  daysToExpiry: number | null;
}

export interface BatchAllocation {
  batchId: string;
  batchNumber: string;
  expiryDate: string | null;
  quantity: number;
}

export interface AllocationResult {
  allocations: BatchAllocation[];
  requested: number;
  allocated: number;
  /** Quantity the usable batches could not cover (0 when complete). */
  shortfall: number;
}

export type BatchProblem = 'blocked' | 'expired' | 'empty' | 'short-expiry';

/** Why a batch cannot be used for this issue, or null when it can. */
export function batchProblem(batch: FefoBatch, minimumShelfLifeDays = 0): BatchProblem | null {
  if (batch.isBlocked) {
    return 'blocked';
  }
  if (batch.isExpired || (batch.daysToExpiry !== null && batch.daysToExpiry < 0)) {
    return 'expired';
  }
  if (batch.available <= 0) {
    return 'empty';
  }
  if (batch.daysToExpiry !== null && batch.daysToExpiry < Math.max(minimumShelfLifeDays, 0)) {
    return 'short-expiry';
  }
  return null;
}

/** Plain-language reason for {@link batchProblem}. */
export function batchProblemText(problem: BatchProblem, batch: FefoBatch, minimumShelfLifeDays = 0): string {
  switch (problem) {
    case 'blocked':
      return batch.blockReason ? `On hold: ${batch.blockReason}` : 'On hold (quality or recall)';
    case 'expired':
      return 'Expired';
    case 'empty':
      return 'No free stock in this batch';
    case 'short-expiry':
      return `Expires in ${batch.daysToExpiry} days, before the ${minimumShelfLifeDays}-day course ends`;
  }
}

/** Usable batches in the order FEFO takes them. */
export function fefoOrder(
  batches: readonly FefoBatch[],
  options: { minimumShelfLifeDays?: number; preferredBatchId?: string | null } = {},
): FefoBatch[] {
  const minimum = options.minimumShelfLifeDays ?? 0;
  return batches
    .map((batch, index) => ({ batch, index }))
    .filter(({ batch }) => batchProblem(batch, minimum) === null)
    .sort((a, b) => {
      const preferred =
        Number(b.batch.batchId === options.preferredBatchId) - Number(a.batch.batchId === options.preferredBatchId);
      if (preferred !== 0) {
        return preferred;
      }
      const aNone = a.batch.expiryDate === null;
      const bNone = b.batch.expiryDate === null;
      if (aNone !== bNone) {
        return aNone ? 1 : -1;
      }
      if (a.batch.expiryDate !== b.batch.expiryDate && a.batch.expiryDate !== null && b.batch.expiryDate !== null) {
        return a.batch.expiryDate < b.batch.expiryDate ? -1 : 1;
      }
      return a.index - b.index;
    })
    .map(({ batch }) => batch);
}

function result(allocations: BatchAllocation[], requested: Dec, allocated: Dec): AllocationResult {
  return {
    allocations,
    requested: toNumber(requested),
    allocated: toNumber(allocated),
    shortfall: toNumber(requested > allocated ? requested - allocated : ZERO),
  };
}

/** FEFO suggestion for a quantity. Always returns what the usable batches can cover (check `shortfall`). */
export function allocateFefo(
  quantity: number,
  batches: readonly FefoBatch[],
  options: { minimumShelfLifeDays?: number; preferredBatchId?: string | null } = {},
): AllocationResult {
  const requested = dec(quantity);
  const allocations: BatchAllocation[] = [];
  let remaining = requested;
  if (requested > ZERO) {
    for (const batch of fefoOrder(batches, options)) {
      if (remaining <= ZERO) {
        break;
      }
      const take = min(dec(batch.available), remaining);
      allocations.push({
        batchId: batch.batchId,
        batchNumber: batch.batchNumber,
        expiryDate: batch.expiryDate,
        quantity: toNumber(take),
      });
      remaining -= take;
    }
  }
  return result(allocations, requested, requested > ZERO ? requested - remaining : ZERO);
}

/**
 * Everything from one batch the user chose (the server issues a chosen batch only, never topping up from
 * another). Unusable batches allocate nothing.
 */
export function allocateFromBatch(quantity: number, batch: FefoBatch, minimumShelfLifeDays = 0): AllocationResult {
  const requested = dec(quantity);
  if (requested <= ZERO || batchProblem(batch, minimumShelfLifeDays) !== null) {
    return result([], requested, ZERO);
  }
  const take = min(dec(batch.available), requested);
  return result(
    [
      {
        batchId: batch.batchId,
        batchNumber: batch.batchNumber,
        expiryDate: batch.expiryDate,
        quantity: toNumber(take),
      },
    ],
    requested,
    take,
  );
}

/** Quantity FEFO could hand out now under the same rules. */
export function usableQuantity(batches: readonly FefoBatch[], minimumShelfLifeDays = 0): number {
  return toNumber(
    fefoOrder(batches, { minimumShelfLifeDays }).reduce((total, batch) => total + dec(batch.available), ZERO),
  );
}

/** Server shelf-life rule for OP dispensing: the course length, capped at 30 days. */
export function dispensingShelfLifeDays(durationDays: number | null | undefined): number {
  return Math.min(Math.max(durationDays ?? 0, 0), 30);
}
