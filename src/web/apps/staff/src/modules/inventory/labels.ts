import type { StatusTone } from '@hc360/ui';

/** Enums arrive as their C# names; these give each value its plain-language label. */

export const ITEM_TYPES = ['Drug', 'Consumable', 'Surgical', 'Implant', 'General', 'Food', 'Linen', 'Spare'] as const;
export type ItemType = (typeof ITEM_TYPES)[number];
export const itemTypeLabel: Record<ItemType, string> = {
  Drug: 'Medicine',
  Consumable: 'Consumable',
  Surgical: 'Surgical',
  Implant: 'Implant',
  General: 'General',
  Food: 'Food and nutrition',
  Linen: 'Linen',
  Spare: 'Spare part',
};

/** Drugs & Cosmetics Rules, 1945 schedules. */
export const DRUG_SCHEDULES = ['None', 'Otc', 'G', 'H', 'H1', 'X'] as const;
export type DrugSchedule = (typeof DRUG_SCHEDULES)[number];
export const scheduleLabel: Record<DrugSchedule, string> = {
  None: 'Not scheduled',
  Otc: 'OTC',
  G: 'Schedule G',
  H: 'Schedule H',
  H1: 'Schedule H1',
  X: 'Schedule X',
};
export const scheduleHint: Record<DrugSchedule, string> = {
  None: 'Not a drug, or not scheduled',
  Otc: 'Over the counter: no prescription needed',
  G: 'Sold with a caution label; no prescription needed',
  H: 'Prescription only',
  H1: 'Prescription only; recorded in the H1 register with patient and prescriber',
  X: 'Narcotic or psychotropic; prescription copy kept and recorded in the register',
};

/** Item.NeedsPrescription: Schedule H, H1, X or a controlled (NDPS) item is never sold without a prescription. */
export function requiresPrescription(schedule: DrugSchedule, isControlled: boolean): boolean {
  return isControlled || schedule === 'H' || schedule === 'H1' || schedule === 'X';
}

/** Item.NeedsRegister: H1, X and controlled (NDPS) items go into the statutory register. */
export function requiresRegister(schedule: DrugSchedule, isControlled: boolean): boolean {
  return isControlled || schedule === 'H1' || schedule === 'X';
}

export const VED_CLASSES = ['Vital', 'Essential', 'Desirable'] as const;
export type VedClass = (typeof VED_CLASSES)[number];
export const vedLabel: Record<VedClass, string> = {
  Vital: 'Vital (life-saving, no stock-out)',
  Essential: 'Essential',
  Desirable: 'Desirable',
};

export const STORE_TYPES = ['Central', 'Pharmacy', 'Ward', 'OT', 'Lab', 'Canteen', 'Cssd'] as const;
export type StoreType = (typeof STORE_TYPES)[number];
export const storeTypeLabel: Record<StoreType, string> = {
  Central: 'Central store',
  Pharmacy: 'Pharmacy',
  Ward: 'Ward sub-store',
  OT: 'Operation theatre',
  Lab: 'Laboratory',
  Canteen: 'Canteen',
  Cssd: 'CSSD',
};

export const MOVEMENT_TYPES = [
  'Grn',
  'Issue',
  'TransferIn',
  'TransferOut',
  'Return',
  'Adjustment',
  'Sale',
  'Dispense',
  'Consumption',
  'ExpiryWriteOff',
  'Opening',
] as const;
export type StockMovementType = (typeof MOVEMENT_TYPES)[number];
export const movementLabel: Record<StockMovementType, string> = {
  Grn: 'Goods receipt',
  Issue: 'Issued',
  TransferIn: 'Transfer in',
  TransferOut: 'Transfer out',
  Return: 'Returned',
  Adjustment: 'Adjustment',
  Sale: 'Counter sale',
  Dispense: 'Dispensed',
  Consumption: 'Consumed',
  ExpiryWriteOff: 'Expiry write-off',
  Opening: 'Opening balance',
};

export const INDENT_STATUSES = [
  'Pending',
  'Approved',
  'PartiallyIssued',
  'Issued',
  'Rejected',
  'Cancelled',
  'Closed',
] as const;
export type IndentStatus = (typeof INDENT_STATUSES)[number];
export const INDENT_PRIORITIES = ['Routine', 'Urgent'] as const;
export type IndentPriority = (typeof INDENT_PRIORITIES)[number];

export const TRANSFER_STATUSES = ['InTransit', 'Received', 'Cancelled'] as const;
export type TransferStatus = (typeof TRANSFER_STATUSES)[number];

export const ADJUSTMENT_REASONS = [
  'Damage',
  'Breakage',
  'Expiry',
  'Loss',
  'Theft',
  'CountVariance',
  'DataCorrection',
  'Other',
] as const;
export type AdjustmentReason = (typeof ADJUSTMENT_REASONS)[number];
export const adjustmentReasonLabel: Record<AdjustmentReason, string> = {
  Damage: 'Damaged',
  Breakage: 'Breakage',
  Expiry: 'Expired stock',
  Loss: 'Lost',
  Theft: 'Theft',
  CountVariance: 'Physical count difference',
  DataCorrection: 'Data correction',
  Other: 'Other',
};
/** The server asks for remarks on these reasons. */
export const REASONS_NEEDING_REMARKS: readonly AdjustmentReason[] = ['Other', 'DataCorrection', 'Theft', 'Loss'];

export const ADJUSTMENT_STATUSES = ['PendingApproval', 'Approved', 'Rejected'] as const;
export type AdjustmentStatus = (typeof ADJUSTMENT_STATUSES)[number];

export const COUNT_STATUSES = ['InProgress', 'Submitted', 'Adjusted', 'Cancelled'] as const;
export type StockCountStatus = (typeof COUNT_STATUSES)[number];

export const ABC_CLASSES = ['A', 'B', 'C'] as const;
export type AbcClass = (typeof ABC_CLASSES)[number];
export const FSN_CLASSES = ['Fast', 'Slow', 'NonMoving'] as const;
export type FsnClass = (typeof FSN_CLASSES)[number];
export const ABC_VED_CATEGORIES = ['I', 'II', 'III'] as const;
export type AbcVedCategory = (typeof ABC_VED_CATEGORIES)[number];
export const abcVedLabel: Record<AbcVedCategory, string> = {
  I: 'Category I: tightest control (AV, AE, AD, BV, CV)',
  II: 'Category II: moderate control (BE, CE, BD)',
  III: 'Category III: routine control (CD)',
};

/** Badge colours for workflow states whose names the shared status map does not know. */
export function workflowTone(status: string): StatusTone | undefined {
  switch (status) {
    case 'PendingApproval':
    case 'PartiallyIssued':
    case 'PartiallyDispensed':
    case 'PartiallyReceived':
      return 'warning';
    case 'InTransit':
    case 'Sent':
      return 'brand';
    case 'Adjusted':
    case 'Posted':
    case 'Received':
      return 'success';
    default:
      return undefined;
  }
}

/** enum → { value, label } options for selects. */
export function labelledOptions<T extends string>(
  values: readonly T[],
  labels: Record<T, string>,
): { value: T; label: string }[] {
  return values.map((value) => ({ value, label: labels[value] }));
}

/** Plain names for the documents behind stock-ledger rows (StockReference modules). */
const REFERENCE_MODULES: Record<string, string> = {
  'supplychain.grn': 'Goods receipt',
  'supplychain.indent': 'Indent issue',
  'supplychain.transfer': 'Store transfer',
  'supplychain.adjustment': 'Stock adjustment',
  'supplychain.opening': 'Opening stock',
  'pharmacy.dispense': 'OP dispensing',
  'pharmacy.sale': 'Counter sale',
  'pharmacy.return': 'Sales return',
  'pharmacy.cancel': 'Invoice cancelled',
  'pharmacy.ip_issue': 'IP issue',
  'pharmacy.ward_return': 'Ward return',
  'specialtycare.ot': 'OT consumption',
};

export function referenceModuleLabel(module: string | null | undefined): string {
  if (!module) {
    return '—';
  }
  return (
    REFERENCE_MODULES[module] ??
    module
      .replace(/^[a-z]+\./, '')
      .replace(/[._]/g, ' ')
      .replace(/^./, (c) => c.toUpperCase())
  );
}
