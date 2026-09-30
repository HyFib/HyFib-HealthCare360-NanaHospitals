import type { StatusTone } from '@hc360/ui';

export const ASSET_CATEGORIES = ['MedicalEquipment', 'IT', 'Furniture', 'Vehicle', 'Other'] as const;
export type AssetCategory = (typeof ASSET_CATEGORIES)[number];
export const assetCategoryLabel: Record<AssetCategory, string> = {
  MedicalEquipment: 'Medical equipment',
  IT: 'IT equipment',
  Furniture: 'Furniture',
  Vehicle: 'Vehicle',
  Other: 'Other',
};

export const ASSET_STATUSES = ['InUse', 'UnderMaintenance', 'Idle', 'Condemned', 'Disposed'] as const;
export type AssetStatus = (typeof ASSET_STATUSES)[number];
export const assetStatusLabel: Record<AssetStatus, string> = {
  InUse: 'In use',
  UnderMaintenance: 'Under maintenance',
  Idle: 'Idle',
  Condemned: 'Condemned',
  Disposed: 'Disposed',
};
export const assetStatusTone: Record<AssetStatus, StatusTone> = {
  InUse: 'success',
  UnderMaintenance: 'warning',
  Idle: 'informative',
  Condemned: 'danger',
  Disposed: 'subtle',
};

export const CRITICALITIES = ['Low', 'Medium', 'High', 'Critical'] as const;
export type AssetCriticality = (typeof CRITICALITIES)[number];
export const criticalityTone: Record<AssetCriticality, StatusTone> = {
  Low: 'informative',
  Medium: 'brand',
  High: 'warning',
  Critical: 'danger',
};

export const DEPRECIATION_METHODS = ['StraightLine', 'WrittenDownValue'] as const;
export type DepreciationMethod = (typeof DEPRECIATION_METHODS)[number];
export const depreciationLabel: Record<DepreciationMethod, string> = {
  StraightLine: 'Straight line (SLM)',
  WrittenDownValue: 'Written-down value (WDV)',
};

export const CONTRACT_TYPES = ['Amc', 'Cmc'] as const;
export type MaintenanceContractType = (typeof CONTRACT_TYPES)[number];
export const contractTypeLabel: Record<MaintenanceContractType, string> = {
  Amc: 'AMC (labour and visits)',
  Cmc: 'CMC (includes parts)',
};

export const TICKET_PRIORITIES = ['Low', 'Medium', 'High', 'Critical'] as const;
export type TicketPriority = (typeof TICKET_PRIORITIES)[number];

export const TICKET_STATUSES = ['Open', 'Assigned', 'InProgress', 'Resolved', 'Closed'] as const;
export type TicketStatus = (typeof TICKET_STATUSES)[number];

export const PM_OUTCOMES = ['Completed', 'CompletedWithIssues', 'NotDone'] as const;
export type PmVisitOutcome = (typeof PM_OUTCOMES)[number];
export const pmOutcomeLabel: Record<PmVisitOutcome, string> = {
  Completed: 'Completed',
  CompletedWithIssues: 'Completed with issues',
  NotDone: 'Not done',
};

export const CALIBRATION_RESULTS = ['Pass', 'Fail', 'Adjusted'] as const;
export type CalibrationResult = (typeof CALIBRATION_RESULTS)[number];
export const calibrationLabel: Record<CalibrationResult, string> = {
  Pass: 'Passed',
  Fail: 'Failed',
  Adjusted: 'Adjusted and passed',
};

export const CONDEMNATION_STATUSES = ['Pending', 'Approved', 'Rejected'] as const;
export type CondemnationStatus = (typeof CONDEMNATION_STATUSES)[number];

/** Kinds of due items from /maintenance/due (MaintenanceService constants). */
export const DUE_KINDS = ['PM', 'Calibration', 'Warranty', 'Contract'] as const;
export type DueKind = (typeof DUE_KINDS)[number];
export const dueKindLabel: Record<string, string> = {
  PM: 'Preventive maintenance',
  Calibration: 'Calibration',
  Warranty: 'Warranty',
  Contract: 'AMC / CMC contract',
};
