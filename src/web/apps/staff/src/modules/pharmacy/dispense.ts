import { dec, toNumber } from '@/modules/inventory/decimal';
import { allocateFefo, allocateFromBatch, batchProblem, batchProblemText, type AllocationResult, type FefoBatch } from '@/modules/inventory/fefo';
import { formatQty } from '@/modules/inventory/format';
import { requiresRegister, type DrugSchedule } from '@/modules/inventory/labels';

/**
 * Pure rules behind the dispensing and issue screens: which batches to pick, whether a different brand is a
 * substitution (SubstitutionPolicy), and what still blocks the hand-over. The server applies the same rules again.
 */

export interface ItemRef {
  itemId: string;
  name: string;
  genericName: string | null;
  strength: string | null;
  /** Unknown when the item master is not visible on this workstation. */
  schedule: DrugSchedule | null;
  isControlled: boolean;
}

export type SubstitutionKind = 'none' | 'substitution' | 'not-equivalent';

/** Letters and digits only, lower case: "Paracetamol 500 mg" and "paracetamol-500MG" compare equal. */
export function normalizeDrugText(value: string): string {
  return Array.from(value)
    .filter((c) => /[\p{L}\p{Nd}]/u.test(c))
    .join('')
    .toLowerCase();
}

/** SubstitutionPolicy.IsEquivalent: null when the generic is unknown on either side. */
export function isGenericEquivalent(
  prescribedGeneric: string | null | undefined,
  prescribedStrength: string | null | undefined,
  dispensedGeneric: string | null | undefined,
  dispensedStrength: string | null | undefined,
): boolean | null {
  if (!prescribedGeneric?.trim() || !dispensedGeneric?.trim()) {
    return null;
  }
  if (normalizeDrugText(prescribedGeneric) !== normalizeDrugText(dispensedGeneric)) {
    return false;
  }
  return !prescribedStrength?.trim() || !dispensedStrength?.trim() || normalizeDrugText(prescribedStrength) === normalizeDrugText(dispensedStrength);
}

/**
 * SubstitutionPolicy.Evaluate without the reason/consent part: the prescribed item, or any brand of a generically
 * prescribed drug in the same strength, is not a substitution; a different generic or strength cannot be given;
 * anything else is a substitution that needs a reason and the prescriber's consent.
 */
export function evaluateSubstitution(
  prescribedItemId: string | null,
  prescribedGeneric: string | null,
  prescribedStrength: string | null,
  dispensed: Pick<ItemRef, 'itemId' | 'genericName' | 'strength'>,
): SubstitutionKind {
  if (prescribedItemId === dispensed.itemId) {
    return 'none';
  }
  const equivalent = isGenericEquivalent(prescribedGeneric, prescribedStrength, dispensed.genericName, dispensed.strength);
  if (prescribedItemId === null && equivalent === true) {
    return 'none';
  }
  return equivalent === false ? 'not-equivalent' : 'substitution';
}

export interface BatchIssueInput {
  /** Quantity to hand over now (base units). */
  quantity: number;
  /** Most that may still be issued on this line (remaining on the prescription or indent); null = no limit. */
  remaining: number | null;
  /** A batch the user picked; null = FEFO. */
  batchId: string | null;
  minimumShelfLifeDays: number;
}

export interface BatchIssuePlan {
  allocation: AllocationResult;
  /** Blocking problems in plain language (empty = ready). */
  problems: string[];
  /** Batches are still loading. */
  pending: boolean;
}

/** Picks the batches for one line and lists what blocks it. */
export function planBatchIssue(input: BatchIssueInput, batches: readonly FefoBatch[] | undefined, unit?: string | null): BatchIssuePlan {
  const problems: string[] = [];
  const empty: AllocationResult = { allocations: [], requested: input.quantity, allocated: 0, shortfall: input.quantity };
  if (!(input.quantity > 0)) {
    return { allocation: empty, problems: ['Enter the quantity to hand over.'], pending: false };
  }
  if (input.remaining !== null && dec(input.quantity) > dec(input.remaining)) {
    problems.push(`Only ${formatQty(input.remaining, unit)} remain to be supplied on this line.`);
  }
  if (!batches) {
    return { allocation: empty, problems, pending: true };
  }
  let allocation: AllocationResult;
  if (input.batchId) {
    const picked = batches.find((b) => b.batchId === input.batchId);
    if (!picked) {
      return { allocation: empty, problems: [...problems, 'The chosen batch is no longer in this store. Pick another batch.'], pending: false };
    }
    const problem = batchProblem(picked, input.minimumShelfLifeDays);
    if (problem) {
      return { allocation: empty, problems: [...problems, `Batch ${picked.batchNumber}: ${batchProblemText(problem, picked, input.minimumShelfLifeDays)}.`], pending: false };
    }
    allocation = allocateFromBatch(input.quantity, picked, input.minimumShelfLifeDays);
    if (allocation.shortfall > 0) {
      problems.push(`Batch ${picked.batchNumber} has only ${formatQty(allocation.allocated, unit)}. Lower the quantity or let the system pick batches.`);
    }
  } else {
    allocation = allocateFefo(input.quantity, batches, { minimumShelfLifeDays: input.minimumShelfLifeDays });
    if (allocation.shortfall > 0) {
      problems.push(
        allocation.allocated > 0
          ? `Only ${formatQty(allocation.allocated, unit)} in stock${input.minimumShelfLifeDays > 0 ? ` that lasts the ${input.minimumShelfLifeDays}-day course` : ''}. Lower the quantity to supply part now.`
          : `Out of stock${input.minimumShelfLifeDays > 0 ? ` (no batch lasts the ${input.minimumShelfLifeDays}-day course)` : ''}.`,
      );
    }
  }
  return { allocation, problems, pending: false };
}

/** Largest quantity that can be handed over now: the smaller of what is left on the line and what is in stock. */
export function suggestedQuantity(remaining: number, usable: number): number {
  return toNumber(dec(remaining) < dec(usable) ? dec(remaining) : dec(usable));
}

/** Substitution problems (reason and consent) for a line, in plain language. */
export function substitutionProblems(kind: SubstitutionKind, reason: string, consent: boolean): string[] {
  if (kind === 'not-equivalent') {
    return ['Only a generic equivalent of the same strength can be substituted.'];
  }
  if (kind === 'substitution') {
    const problems: string[] = [];
    if (!reason.trim()) {
      problems.push('Give the reason for the substitution.');
    }
    if (!consent) {
      problems.push("Record the prescriber's consent to the substitution.");
    }
    return problems;
  }
  return [];
}

/** Names of the items that need the explicit controlled-medicine confirmation (Schedule H1/X or NDPS). */
export function controlledItems(items: readonly { name: string; schedule: DrugSchedule | null; isControlled: boolean; lineIsControlled?: boolean }[]): string[] {
  const names = items
    .filter((i) => i.isControlled || i.lineIsControlled === true || (i.schedule !== null && requiresRegister(i.schedule, i.isControlled)))
    .map((i) => i.name);
  return Array.from(new Set(names));
}

/** Items from a server error's `data.items` (controlled-confirmation or prescription-required refusals). */
export function itemsFromErrorData(data: unknown): string[] {
  if (data && typeof data === 'object' && 'items' in data && Array.isArray((data as { items: unknown }).items)) {
    return (data as { items: unknown[] }).items.filter((i): i is string => typeof i === 'string');
  }
  return [];
}
