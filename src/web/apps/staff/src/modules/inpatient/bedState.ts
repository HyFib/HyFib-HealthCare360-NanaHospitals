/**
 * Bed states, legend, allowed actions and alert wording for the bed board. Pure data (no React), so it is unit tested
 * and shared by the board, the bed picker, the ward widgets and the nursing station. The transition table mirrors the
 * server's `BedStateMachine`; the server validates every change again.
 */

export type BedStatus = 'Vacant' | 'Occupied' | 'Reserved' | 'Cleaning' | 'Blocked' | 'Maintenance';

export const BED_STATUSES: readonly BedStatus[] = ['Vacant', 'Occupied', 'Reserved', 'Cleaning', 'Blocked', 'Maintenance'];

/** Colour family of a state. The tile also shows an icon and the state word, so colour is never the only signal. */
export type BedTone = 'green' | 'blue' | 'marigold' | 'teal' | 'red' | 'grape' | 'neutral';

export type BedIconKey = 'vacant' | 'occupied' | 'reserved' | 'cleaning' | 'blocked' | 'maintenance' | 'unknown';

export interface BedStateInfo {
  status: BedStatus | 'Unknown';
  label: string;
  description: string;
  tone: BedTone;
  icon: BedIconKey;
  /** Counts as a bed in service for the occupancy rate (blocked and maintenance beds do not). */
  inService: boolean;
}

export const BED_STATE: Record<BedStatus, BedStateInfo> = {
  Vacant: { status: 'Vacant', label: 'Vacant', description: 'Clean and ready for a patient', tone: 'green', icon: 'vacant', inService: true },
  Occupied: { status: 'Occupied', label: 'Occupied', description: 'A patient is in this bed', tone: 'blue', icon: 'occupied', inService: true },
  Reserved: { status: 'Reserved', label: 'Reserved', description: 'Held for an expected patient', tone: 'marigold', icon: 'reserved', inService: true },
  Cleaning: { status: 'Cleaning', label: 'Cleaning', description: 'Waiting for or under terminal cleaning', tone: 'teal', icon: 'cleaning', inService: true },
  Blocked: { status: 'Blocked', label: 'Blocked', description: 'Taken out of use (see reason)', tone: 'red', icon: 'blocked', inService: false },
  Maintenance: { status: 'Maintenance', label: 'Maintenance', description: 'Under repair; goes to cleaning afterwards', tone: 'grape', icon: 'maintenance', inService: false },
};

const UNKNOWN_STATE: BedStateInfo = { status: 'Unknown', label: 'Unknown', description: 'Status not recognised', tone: 'neutral', icon: 'unknown', inService: false };

export function bedStateInfo(status: string | null | undefined): BedStateInfo {
  return status && status in BED_STATE ? BED_STATE[status as BedStatus] : UNKNOWN_STATE;
}

/** The legend shown above the bed board, in the order staff read it. */
export const BED_LEGEND: readonly BedStateInfo[] = BED_STATUSES.map((status) => BED_STATE[status]);

/** Allowed transitions (mirror of the server's BedStateMachine). */
export const BED_TRANSITIONS: Record<BedStatus, readonly BedStatus[]> = {
  Vacant: ['Occupied', 'Reserved', 'Cleaning', 'Blocked', 'Maintenance'],
  Reserved: ['Occupied', 'Vacant'],
  Occupied: ['Cleaning'],
  Cleaning: ['Vacant', 'Blocked', 'Maintenance'],
  Blocked: ['Vacant', 'Cleaning', 'Maintenance'],
  Maintenance: ['Cleaning', 'Blocked'],
};

export function canMove(from: string, to: string): boolean {
  return from in BED_TRANSITIONS && (BED_TRANSITIONS[from as BedStatus] as readonly string[]).includes(to);
}

export type BedAction =
  | 'admit'
  | 'reserve'
  | 'cancelReservation'
  | 'sendForCleaning'
  | 'startCleaning'
  | 'completeCleaning'
  | 'block'
  | 'unblock'
  | 'startMaintenance'
  | 'endMaintenance';

export interface BedPermissions {
  admit: boolean;
  manage: boolean;
  housekeeping: boolean;
}

export const BED_ACTION_LABEL: Record<BedAction, string> = {
  admit: 'Admit a patient here',
  reserve: 'Reserve bed',
  cancelReservation: 'Cancel reservation',
  sendForCleaning: 'Send for cleaning',
  startCleaning: 'Start cleaning',
  completeCleaning: 'Mark as cleaned',
  block: 'Block bed',
  unblock: 'Unblock (make vacant)',
  startMaintenance: 'Send for maintenance',
  endMaintenance: 'Repair done (send for cleaning)',
};

/**
 * Actions a user may take on a bed in a given state, most common first. Occupied beds are released only by discharge,
 * transfer or cancellation (never from the board), so they offer no status action here.
 */
export function bedActions(status: string, permissions: BedPermissions): BedAction[] {
  const actions: BedAction[] = [];
  const allowed = (target: BedStatus) => canMove(status, target);
  if (allowed('Occupied') && permissions.admit) {
    actions.push('admit');
  }
  if (status === 'Cleaning' && permissions.housekeeping) {
    actions.push('startCleaning', 'completeCleaning');
  }
  if (!permissions.manage) {
    // Housekeeping may send a vacant or blocked bed for cleaning; ending maintenance stays with bed managers.
    if (permissions.housekeeping && (status === 'Vacant' || status === 'Blocked')) {
      actions.push('sendForCleaning');
    }
    return actions;
  }
  if (allowed('Reserved')) {
    actions.push('reserve');
  }
  if (status === 'Reserved' && allowed('Vacant')) {
    actions.push('cancelReservation');
  }
  if (status === 'Blocked' && allowed('Vacant')) {
    actions.push('unblock');
  }
  if (status === 'Maintenance' && allowed('Cleaning')) {
    actions.push('endMaintenance');
  } else if (status !== 'Occupied' && allowed('Cleaning')) {
    actions.push('sendForCleaning');
  }
  if (allowed('Blocked')) {
    actions.push('block');
  }
  if (allowed('Maintenance')) {
    actions.push('startMaintenance');
  }
  return actions;
}

export type BedAlert =
  | 'Mlc'
  | 'Restricted'
  | 'HighFallRisk'
  | 'PressureInjuryRisk'
  | 'SeverePain'
  | 'MedicationOverdue'
  | 'DischargeInitiated'
  | 'DischargeDueToday'
  | 'ReservationExpired'
  | 'CleaningDelayed';

export type AlertSeverity = 'danger' | 'warning' | 'informative';

export interface BedAlertInfo {
  label: string;
  description: string;
  severity: AlertSeverity;
}

export const BED_ALERT: Record<BedAlert, BedAlertInfo> = {
  Mlc: { label: 'MLC', description: 'Medico-legal case', severity: 'warning' },
  Restricted: { label: 'Restricted', description: 'Restricted record: access is audited', severity: 'informative' },
  HighFallRisk: { label: 'Fall risk', description: 'High fall risk (Morse 45 or more): fall precautions', severity: 'danger' },
  PressureInjuryRisk: { label: 'Pressure risk', description: 'Pressure-injury risk (Braden 12 or less): turn two-hourly', severity: 'danger' },
  SeverePain: { label: 'Severe pain', description: 'Last pain score 7 or more: reassess', severity: 'danger' },
  MedicationOverdue: { label: 'Medicine overdue', description: 'A scheduled dose is overdue', severity: 'danger' },
  DischargeInitiated: { label: 'Discharging', description: 'Discharge started, waiting for billing clearance', severity: 'informative' },
  DischargeDueToday: { label: 'Discharge due', description: 'Expected discharge date is today or earlier', severity: 'warning' },
  ReservationExpired: { label: 'Reservation lapsed', description: 'The reservation time has passed; the bed can be given to anyone', severity: 'warning' },
  CleaningDelayed: { label: 'Cleaning delayed', description: 'Waiting more than 60 minutes for cleaning', severity: 'warning' },
};

const SEVERITY_ORDER: Record<AlertSeverity, number> = { danger: 0, warning: 1, informative: 2 };

export function bedAlertInfo(alert: string): BedAlertInfo {
  return alert in BED_ALERT ? BED_ALERT[alert as BedAlert] : { label: alert, description: alert, severity: 'informative' };
}

/** Alerts ordered most serious first (tiles show the first few). */
export function sortAlerts(alerts: readonly string[]): string[] {
  return [...alerts].sort((a, b) => SEVERITY_ORDER[bedAlertInfo(a).severity] - SEVERITY_ORDER[bedAlertInfo(b).severity]);
}

export interface BedCountsLike {
  total: number;
  occupied: number;
  vacant: number;
  reserved: number;
  cleaning: number;
  blocked: number;
  maintenance: number;
}

/** Counts by state (same arithmetic as the server's BedCounts.From). */
export function countBeds(statuses: readonly string[]): BedCountsLike {
  const count = (status: BedStatus) => statuses.filter((s) => s === status).length;
  return {
    total: statuses.length,
    occupied: count('Occupied'),
    vacant: count('Vacant'),
    reserved: count('Reserved'),
    cleaning: count('Cleaning'),
    blocked: count('Blocked'),
    maintenance: count('Maintenance'),
  };
}

/** Bed occupancy: occupied ÷ beds in service (total minus blocked and maintenance) × 100, one decimal. */
export function occupancyPercent(counts: BedCountsLike): number {
  const inService = counts.total - counts.blocked - counts.maintenance;
  return inService <= 0 ? 0 : Math.round((counts.occupied * 1000) / inService) / 10;
}

/** Occupancy band for colouring bars: under 70 % comfortable, 70–90 % busy, 90 % and above full. */
export function occupancyBand(percent: number): 'low' | 'busy' | 'full' {
  return percent >= 90 ? 'full' : percent >= 70 ? 'busy' : 'low';
}

export interface SearchableBed {
  label: string;
  roomNumber?: string | null;
  occupant?: { patientName: string; uhid: string; admissionNumber: string } | null;
}

/** Case-insensitive match on bed label, room, patient name, UHID or admission number. */
export function matchesBedSearch(bed: SearchableBed, term: string): boolean {
  const needle = term.trim().toLowerCase();
  if (!needle) {
    return true;
  }
  const haystack = [bed.label, bed.roomNumber, bed.occupant?.patientName, bed.occupant?.uhid, bed.occupant?.admissionNumber];
  return haystack.some((value) => value?.toLowerCase().includes(needle));
}

/** Labels the server gives bulk-created beds: "{PREFIX}-{nn}" (upper case, at least two digits), e.g. GWM-01…GWM-12. */
export function bulkBedLabels(prefix: string, startNumber: number, count: number): string[] {
  const clean = prefix.trim().toUpperCase();
  if (!clean || !Number.isInteger(startNumber) || !Number.isInteger(count) || count < 1) {
    return [];
  }
  return Array.from({ length: count }, (_, i) => `${clean}-${String(startNumber + i).padStart(2, '0')}`);
}

/** The next free number for a prefix, from existing labels like "GWM-07" (so bulk adds continue the series). */
export function nextBedNumber(prefix: string, labels: readonly string[]): number {
  const head = `${prefix.trim().toUpperCase()}-`;
  const numbers = labels
    .map((label) => label.trim().toUpperCase())
    .filter((label) => label.startsWith(head))
    .map((label) => label.slice(head.length))
    .filter((rest) => /^\d+$/.test(rest))
    .map(Number);
  return numbers.length ? Math.max(...numbers) + 1 : 1;
}
