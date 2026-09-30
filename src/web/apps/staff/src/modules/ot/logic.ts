import { formatDateOnly, formatInr, formatNumber, isApiError } from '@hc360/core';
import type {
  ChecklistItemDto,
  OtCalendarBooking,
  OtStoreBatch,
  OtStoreItem,
  SafetyPhase,
  SchedulingConflict,
  SurgeryDetail,
  SurgeryStatus,
} from './api';
import { phaseLabel, sideShort } from './labels';
import { minutesIntoIstDay } from './shared/datetime';

/**
 * Pure OT rules for the screens. They mirror the server (`SurgeryBooking`, `SurgeryScheduling`) so buttons are
 * disabled with the same explanation the server would give; the server still enforces every rule.
 */

export interface Gate {
  allowed: boolean;
  reason: string | null;
}

const ALLOWED: Gate = { allowed: true, reason: null };
const refuse = (reason: string): Gate => ({ allowed: false, reason });

/** The fields of a booking the gates look at (a `SurgeryDetail` satisfies it). */
export type GateInput = Pick<SurgeryDetail, 'status' | 'preOpComplete' | 'safetyChecklist'>;

export function isPhaseComplete(detail: Pick<SurgeryDetail, 'safetyChecklist'>, phase: SafetyPhase): boolean {
  return detail.safetyChecklist.some((p) => p.phase === phase && p.isComplete);
}

/** Statuses that hold a theatre slot (a completed case no longer blocks it). */
export const BLOCKING_STATUSES: readonly SurgeryStatus[] = ['Scheduled', 'InPreOp', 'InProgress'];

const DAY_OF_SURGERY: readonly SurgeryStatus[] = ['Scheduled', 'InPreOp'];

export function prerequisiteOf(phase: SafetyPhase): SafetyPhase | null {
  return phase === 'TimeOut' ? 'SignIn' : phase === 'SignOut' ? 'TimeOut' : null;
}

export interface PhaseGate extends Gate {
  /** The phase is complete: its items can no longer change. */
  locked: boolean;
}

/**
 * Whether items of a WHO phase can be confirmed now — same order of checks as `SurgeryBooking.ConfirmSafety`:
 * locked when complete, phases in order, Sign-in/Time-out on the day of surgery, Sign-out only during surgery,
 * and the Sign-in only after a complete pre-operative checklist.
 */
export function phaseGate(detail: GateInput, phase: SafetyPhase): PhaseGate {
  if (isPhaseComplete(detail, phase)) {
    return { allowed: false, locked: true, reason: `${phaseLabel[phase]} is complete and locked.` };
  }
  if (detail.status === 'Cancelled') {
    return { allowed: false, locked: false, reason: 'This booking is cancelled.' };
  }
  const before = prerequisiteOf(phase);
  if (before && !isPhaseComplete(detail, before)) {
    return { allowed: false, locked: false, reason: `Complete the ${phaseLabel[before]} first.` };
  }
  if (phase === 'SignOut') {
    if (detail.status !== 'InProgress') {
      return {
        allowed: false,
        locked: false,
        reason: 'The Sign-out is done at the end of surgery, after it has started.',
      };
    }
  } else if (!DAY_OF_SURGERY.includes(detail.status)) {
    const reason =
      detail.status === 'Requested' || detail.status === 'Postponed'
        ? 'Schedule the surgery first. The checklist is done on the day of surgery.'
        : `The ${phaseLabel[phase]} is done on the day of surgery for a scheduled patient.`;
    return { allowed: false, locked: false, reason };
  }
  if (phase === 'SignIn' && !detail.preOpComplete) {
    return { allowed: false, locked: false, reason: 'Complete the pre-operative checklist first.' };
  }
  return { allowed: true, locked: false, reason: null };
}

/** Pre-operative checklist: editable until the WHO Sign-in, while the case has not started or closed. */
export function preOpGate(detail: GateInput): Gate {
  if (isPhaseComplete(detail, 'SignIn')) {
    return refuse('The pre-operative checklist is locked after the WHO Sign-in.');
  }
  if (!(['Requested', 'Scheduled', 'InPreOp', 'Postponed'] as SurgeryStatus[]).includes(detail.status)) {
    return refuse('The pre-operative checklist can no longer be changed for this booking.');
  }
  return ALLOWED;
}

export function receiveGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  switch (detail.status) {
    case 'Scheduled':
      return ALLOWED;
    case 'Requested':
    case 'Postponed':
      return refuse('Schedule the surgery first.');
    case 'InPreOp':
      return refuse('The patient has already been received.');
    default:
      return refuse('Only a scheduled surgery can be received in pre-op.');
  }
}

/** Knife to skin — never before the WHO Sign-in and Time-out (`SurgeryBooking.Start`). */
export function startGate(detail: GateInput): Gate {
  switch (detail.status) {
    case 'Scheduled':
    case 'InPreOp':
      break;
    case 'InProgress':
      return refuse('Surgery has already started.');
    case 'Completed':
      return refuse('Surgery is complete.');
    case 'Cancelled':
      return refuse('This booking is cancelled.');
    default:
      return refuse('Schedule the surgery first.');
  }
  const missing = (['SignIn', 'TimeOut'] as SafetyPhase[]).filter((phase) => !isPhaseComplete(detail, phase));
  if (missing.length > 0) {
    return refuse(
      `Surgery cannot start until the ${missing.map((p) => phaseLabel[p]).join(' and ')} ${missing.length > 1 ? 'are' : 'is'} complete.`,
    );
  }
  return ALLOWED;
}

/** Wheels-out — never before the WHO Sign-out (`SurgeryBooking.Complete`). */
export function completeGate(detail: GateInput): Gate {
  if (detail.status === 'Completed') {
    return refuse('Surgery is already complete.');
  }
  if (detail.status !== 'InProgress') {
    return refuse('Only a surgery in progress can be completed.');
  }
  if (!isPhaseComplete(detail, 'SignOut')) {
    return refuse('Surgery cannot be completed until the WHO Sign-out is complete.');
  }
  return ALLOWED;
}

export function scheduleGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return (['Requested', 'Scheduled', 'Postponed'] as SurgeryStatus[]).includes(detail.status)
    ? ALLOWED
    : refuse('Only requested, scheduled or postponed surgeries can be given a slot.');
}

export function planGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return (['Requested', 'Scheduled', 'InPreOp', 'Postponed'] as SurgeryStatus[]).includes(detail.status)
    ? ALLOWED
    : refuse('The surgical plan cannot be changed once surgery has started or the booking is closed.');
}

export function postponeGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return (['Requested', 'Scheduled', 'InPreOp'] as SurgeryStatus[]).includes(detail.status)
    ? ALLOWED
    : refuse('Only surgeries that have not started can be postponed.');
}

export function cancelGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return (['InProgress', 'Completed', 'Cancelled'] as SurgeryStatus[]).includes(detail.status)
    ? refuse('A surgery that has started or is closed cannot be cancelled.')
    : ALLOWED;
}

export function materialsGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return (['InPreOp', 'InProgress', 'Completed'] as SurgeryStatus[]).includes(detail.status)
    ? ALLOWED
    : refuse(
        'Implants and consumables are recorded from pre-op (after the patient is received) until the surgery is completed.',
      );
}

export function operationNoteGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return detail.status === 'InProgress' || detail.status === 'Completed'
    ? ALLOWED
    : refuse('The operation note is written once surgery has started.');
}

export function anaesthesiaGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return detail.status === 'Requested' || detail.status === 'Cancelled'
    ? refuse('The anaesthesia record is kept for scheduled surgeries.')
    : ALLOWED;
}

// ---------------------------------------------------------------- implants and consumables from the OT store

/** Why the OT store cannot be searched: the facility has no active OT store, or more than one (422 from the server). */
export type OtStoreProblem = 'no-store' | 'ambiguous';

export function otStoreProblem(error: unknown): OtStoreProblem | null {
  const code = isApiError(error) ? error.code : undefined;
  return code === 'stock.no_store' ? 'no-store' : code === 'stock.store_ambiguous' ? 'ambiguous' : null;
}

/** The listed batch whose lot is the one on the form (case and spaces ignored), if any. */
export function batchForLot(item: Pick<OtStoreItem, 'batches'> | null, lotNumber: string): OtStoreBatch | null {
  const lot = lotNumber.trim().toUpperCase();
  return lot && item ? (item.batches.find((b) => b.batchNumber.trim().toUpperCase() === lot) ?? null) : null;
}

/** What the OT store can issue: a named lot is issued only from that batch, otherwise FEFO draws on all the stock. */
export function issuableQuantity(item: Pick<OtStoreItem, 'available' | 'batches'>, lotNumber: string): number {
  return batchForLot(item, lotNumber)?.available ?? item.available;
}

/**
 * The price per unit once a batch is chosen: that batch's MRP, unless the user typed a price of their own (anything
 * other than the MRP filled in earlier), which is kept.
 */
export function priceForBatch(current: number | null, filledMrp: number | null, batchMrp: number): number {
  return current === null || current === filledMrp ? batchMrp : current;
}

/** "Lot B123 · expires 31 Mar 2027 · 4 Nos available · MRP ₹1,250.00". */
export function batchLabel(batch: OtStoreBatch, baseUnit: string): string {
  return [
    `Lot ${batch.batchNumber}`,
    batch.expiryDate ? `expires ${formatDateOnly(batch.expiryDate)}` : 'no expiry date',
    `${formatNumber(batch.available)} ${baseUnit} available`,
    `MRP ${formatInr(batch.mrp)}`,
  ].join(' · ');
}

// ---------------------------------------------------------------- day-of-surgery steps

export type StepId = 'schedule' | 'pre-op' | 'receive' | 'sign-in' | 'time-out' | 'start' | 'sign-out' | 'complete';
export type StepState = 'done' | 'current' | 'upcoming' | 'skipped' | 'stopped';

export const STEP_IDS: readonly StepId[] = [
  'schedule',
  'pre-op',
  'receive',
  'sign-in',
  'time-out',
  'start',
  'sign-out',
  'complete',
];

export interface SurgeryStep {
  id: StepId;
  title: string;
  moment: string;
  state: StepState;
  at: string | null;
}

export function lastRecordedAt(items: ChecklistItemDto[]): string | null {
  const times = items.map((i) => i.recordedAt).filter((at): at is string => Boolean(at));
  return times.length === 0 ? null : times.reduce((latest, at) => (Date.parse(at) > Date.parse(latest) ? at : latest));
}

type StepInput = Pick<
  SurgeryDetail,
  | 'status'
  | 'scheduledStart'
  | 'preOpComplete'
  | 'preOpChecklist'
  | 'safetyChecklist'
  | 'receivedInPreOpAt'
  | 'startedAt'
  | 'completedAt'
>;

/** The day of surgery in order. "Received in pre-op" is optional: it shows as skipped when later steps happened. */
export function surgerySteps(detail: StepInput): SurgeryStep[] {
  const phase = (p: SafetyPhase) => detail.safetyChecklist.find((x) => x.phase === p);
  const slotHeld =
    detail.scheduledStart !== null &&
    !(['Requested', 'Postponed', 'Cancelled'] as SurgeryStatus[]).includes(detail.status);
  const raw: { id: StepId; title: string; moment: string; done: boolean; at: string | null; optional?: boolean }[] = [
    {
      id: 'schedule',
      title: 'Theatre and time booked',
      moment: 'OT coordinator',
      done: slotHeld,
      at: detail.scheduledStart,
    },
    {
      id: 'pre-op',
      title: 'Pre-operative checklist',
      moment: 'Ward or pre-op nurse',
      done: detail.preOpComplete,
      at: lastRecordedAt(detail.preOpChecklist),
    },
    {
      id: 'receive',
      title: 'Received in pre-op',
      moment: 'Pre-op holding area',
      done: detail.receivedInPreOpAt !== null,
      at: detail.receivedInPreOpAt,
      optional: true,
    },
    {
      id: 'sign-in',
      title: phaseLabel.SignIn,
      moment: 'Before induction of anaesthesia',
      done: phase('SignIn')?.isComplete ?? false,
      at: phase('SignIn')?.completedAt ?? null,
    },
    {
      id: 'time-out',
      title: phaseLabel.TimeOut,
      moment: 'Before skin incision',
      done: phase('TimeOut')?.isComplete ?? false,
      at: phase('TimeOut')?.completedAt ?? null,
    },
    {
      id: 'start',
      title: 'Surgery started',
      moment: 'Knife to skin',
      done: detail.startedAt !== null,
      at: detail.startedAt,
    },
    {
      id: 'sign-out',
      title: phaseLabel.SignOut,
      moment: 'Before the patient leaves the theatre',
      done: phase('SignOut')?.isComplete ?? false,
      at: phase('SignOut')?.completedAt ?? null,
    },
    {
      id: 'complete',
      title: 'Patient out of theatre',
      moment: 'Surgery complete',
      done: detail.completedAt !== null,
      at: detail.completedAt,
    },
  ];

  let currentFound = false;
  return raw.map((step, index) => {
    const { optional, done, ...rest } = step;
    if (done) {
      return { ...rest, state: 'done' as const };
    }
    if (optional && raw.slice(index + 1).some((later) => later.done)) {
      return { ...rest, state: 'skipped' as const };
    }
    if (detail.status === 'Cancelled') {
      return { ...rest, state: 'stopped' as const };
    }
    if (!currentFound) {
      currentFound = true;
      return { ...rest, state: 'current' as const };
    }
    return { ...rest, state: 'upcoming' as const };
  });
}

/** The step to open by default: the first one still to do (or the last step when everything is done). */
export function currentStepId(detail: StepInput): StepId {
  const steps = surgerySteps(detail);
  return steps.find((s) => s.state === 'current')?.id ?? (detail.status === 'Completed' ? 'complete' : 'schedule');
}

export function whoProgress(item: { signInDone: boolean; timeOutDone: boolean; signOutDone: boolean }): number {
  return [item.signInDone, item.timeOutDone, item.signOutDone].filter(Boolean).length;
}

/** "Total knee replacement — LEFT". */
export function procedureWithSide(name: string, laterality: SurgeryDetail['laterality']): string {
  const side = sideShort(laterality);
  return side ? `${name} — ${side}` : name;
}

// ---------------------------------------------------------------- scheduling

/** Half-open overlap like the server: a case ending at 10:00 does not clash with one starting at 10:00. */
export function overlaps(aStart: string, aEnd: string, bStart: string, bEnd: string): boolean {
  return Date.parse(aStart) < Date.parse(bEnd) && Date.parse(bStart) < Date.parse(aEnd);
}

/** Bookings on the board that would clash with a proposed slot (a hint only; the server decides under a lock). */
export function localClashes(
  slot: { start: string; end: string; bookingId?: string | null },
  bookings: readonly OtCalendarBooking[],
): OtCalendarBooking[] {
  return bookings.filter(
    (b) =>
      b.id !== slot.bookingId &&
      BLOCKING_STATUSES.includes(b.status) &&
      b.start !== null &&
      b.end !== null &&
      overlaps(slot.start, slot.end, b.start, b.end),
  );
}

/** The clashes the server reported with a 409 `surgery.slot_conflict`, or null for any other error. */
export function slotConflicts(error: unknown): SchedulingConflict[] | null {
  if (!isApiError(error) || error.code !== 'surgery.slot_conflict') {
    return null;
  }
  return Array.isArray(error.data) ? (error.data as SchedulingConflict[]) : [];
}

export const DURATION_PRESETS: readonly number[] = [30, 60, 90, 120, 180, 240];

// ---------------------------------------------------------------- calendar layout

export interface TimeWindow {
  /** Minutes from IST midnight of the day shown. */
  start: number;
  end: number;
}

export const DEFAULT_WINDOW: TimeWindow = { start: 8 * 60, end: 20 * 60 };

/** Hours shown on the day board: 8 am–8 pm, widened to whole hours around any case that falls outside. */
export function dayWindow(bookings: readonly { start: string | null; end: string | null }[], date: string): TimeWindow {
  let start = DEFAULT_WINDOW.start;
  let end = DEFAULT_WINDOW.end;
  for (const booking of bookings) {
    if (!booking.start || !booking.end) {
      continue;
    }
    const s = Math.max(0, minutesIntoIstDay(booking.start, date));
    const e = Math.min(1440, minutesIntoIstDay(booking.end, date));
    if (e <= 0 || s >= 1440 || e <= s) {
      continue;
    }
    start = Math.min(start, Math.floor(s / 60) * 60);
    end = Math.max(end, Math.ceil(e / 60) * 60);
  }
  return { start, end };
}

export interface PlacedBlock<T> {
  item: T;
  /** Minutes from midnight, clipped to the window. */
  startMinute: number;
  endMinute: number;
  clippedStart: boolean;
  clippedEnd: boolean;
  /** Side-by-side position when cases overlap (an overrun), 0-based, out of `lanes`. */
  lane: number;
  lanes: number;
}

/** Positions one theatre's cases on the day board; overlapping cases share the column side by side. */
export function placeBlocks<T extends { start: string | null; end: string | null }>(
  items: readonly T[],
  date: string,
  window: TimeWindow,
): PlacedBlock<T>[] {
  const blocks = items
    .filter((item): item is T & { start: string; end: string } => item.start !== null && item.end !== null)
    .map((item) => {
      const s = minutesIntoIstDay(item.start, date);
      const e = minutesIntoIstDay(item.end, date);
      return {
        item,
        startMinute: Math.max(s, window.start),
        endMinute: Math.min(e, window.end),
        clippedStart: s < window.start,
        clippedEnd: e > window.end,
      };
    })
    .filter((b) => b.endMinute > b.startMinute)
    .sort((a, b) => a.startMinute - b.startMinute || a.endMinute - b.endMinute);

  const placed: PlacedBlock<T>[] = [];
  let cluster: ((typeof blocks)[number] & { lane: number })[] = [];
  let laneEnds: number[] = [];
  let clusterEnd = Number.NEGATIVE_INFINITY;

  const flush = () => {
    const lanes = Math.max(1, laneEnds.length);
    for (const block of cluster) {
      placed.push({ ...block, lanes });
    }
    cluster = [];
    laneEnds = [];
  };

  for (const block of blocks) {
    if (block.startMinute >= clusterEnd) {
      flush();
      clusterEnd = Number.NEGATIVE_INFINITY;
    }
    let lane = laneEnds.findIndex((end) => end <= block.startMinute);
    if (lane < 0) {
      lane = laneEnds.length;
      laneEnds.push(block.endMinute);
    } else {
      laneEnds[lane] = block.endMinute;
    }
    cluster.push({ ...block, lane });
    clusterEnd = Math.max(clusterEnd, block.endMinute);
  }
  flush();
  return placed;
}

// ---------------------------------------------------------------- utilisation

export interface UtilisationTotals {
  casesCompleted: number;
  casesCancelled: number;
  casesPostponed: number;
  bookedMinutes: number;
  actualMinutes: number;
  availableMinutes: number;
  utilisationPercent: number | null;
  firstCaseOnTimePercent: number | null;
}

export function percent(part: number, whole: number): number | null {
  return whole <= 0 ? null : Math.round((part * 1000) / whole) / 10;
}

/** Totals across theatres, computed the same way as the server's per-theatre figures. */
export function utilisationTotals(
  rows: readonly {
    metrics: {
      casesCompleted: number;
      casesCancelled: number;
      casesPostponed: number;
      bookedMinutes: number;
      actualMinutes: number;
      availableMinutes: number;
      firstCases: number;
      firstCasesOnTime: number;
    };
  }[],
): UtilisationTotals {
  const sum = (pick: (m: (typeof rows)[number]['metrics']) => number) =>
    rows.reduce((total, row) => total + pick(row.metrics), 0);
  const actual = sum((m) => m.actualMinutes);
  const available = sum((m) => m.availableMinutes);
  return {
    casesCompleted: sum((m) => m.casesCompleted),
    casesCancelled: sum((m) => m.casesCancelled),
    casesPostponed: sum((m) => m.casesPostponed),
    bookedMinutes: sum((m) => m.bookedMinutes),
    actualMinutes: actual,
    availableMinutes: available,
    utilisationPercent: percent(actual, available),
    firstCaseOnTimePercent: percent(
      sum((m) => m.firstCasesOnTime),
      sum((m) => m.firstCases),
    ),
  };
}
