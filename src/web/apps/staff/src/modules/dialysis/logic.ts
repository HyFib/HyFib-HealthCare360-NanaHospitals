import type {
  DialysisBoard,
  DialysisMachineStatus,
  DialysisSessionDetail,
  DialysisSessionStatus,
  DialysisShift,
  DialyzerDto,
  DialyzerReusePolicy,
  SeroStatus,
} from './api';
import { machineStatusLabel, seroInSentence } from './labels';

/**
 * Pure dialysis rules for the screens. They mirror the server (`SeroSegregation`, `DialysisMachine.EnsureReadyFor`,
 * `Dialyzer.EnsureUsableFor`) so an unsafe choice is disabled with the same reason the server would give.
 */

export interface MachineLike {
  code: string;
  seroDedication: SeroStatus;
  status: DialysisMachineStatus;
  needsDisinfection: boolean;
  isActive?: boolean;
}

/** What the machine is being chosen for: booking a shift, assigning it, or connecting the patient now. */
export type MachineUse = 'schedule' | 'assign' | 'start';

export interface MachineCheck {
  /** False when the server would refuse it. */
  eligible: boolean;
  /** Same sero category as the patient (the infection-control rule). */
  seroMatch: boolean;
  blockers: string[];
  /** Fine for now, but needs action before the patient is connected. */
  warnings: string[];
}

/** The sero rule: a patient may only be dialysed on a machine dedicated to exactly their category. */
export function isSeroCompatible(patient: SeroStatus, machine: SeroStatus): boolean {
  return patient === machine;
}

export function seroMismatchReason(machine: MachineLike, patient: SeroStatus): string {
  return `Machine ${machine.code} is dedicated to ${seroInSentence[machine.seroDedication]} patients; this patient is ${seroInSentence[patient]}.`;
}

/**
 * Whether a machine can be used for a patient, and why not. Scheduling and assigning check the sero category (and
 * that no one else holds the machine in that shift); connecting also needs the machine in service, free and
 * disinfected after its last session.
 */
export function checkMachine(machine: MachineLike, patientSero: SeroStatus | null, use: MachineUse, bookedByOther = false): MachineCheck {
  const blockers: string[] = [];
  const warnings: string[] = [];
  const seroMatch = patientSero !== null && isSeroCompatible(patientSero, machine.seroDedication);

  if (patientSero === null) {
    blockers.push('Enrol the patient with their sero status first.');
  } else if (!seroMatch) {
    blockers.push(seroMismatchReason(machine, patientSero));
  }
  if (machine.isActive === false || machine.status === 'Maintenance' || machine.status === 'Retired') {
    blockers.push(`Machine ${machine.code} is out of service (${machineStatusLabel[machine.status].toLowerCase()}).`);
  }
  if (bookedByOther) {
    blockers.push(`Machine ${machine.code} is booked for another patient in this shift.`);
  }
  if (use === 'start') {
    if (machine.status === 'InUse') {
      blockers.push(`Machine ${machine.code} is running another session.`);
    }
    if (machine.needsDisinfection) {
      blockers.push(`Record the disinfection of machine ${machine.code} before connecting the next patient.`);
    }
  } else {
    if (machine.status === 'InUse') {
      warnings.push('Running a session now.');
    }
    if (machine.needsDisinfection) {
      warnings.push('Needs disinfection before the patient is connected.');
    }
  }
  return { eligible: blockers.length === 0, seroMatch, blockers, warnings };
}

/** Statuses that hold a machine for a shift on the server (a completed session keeps its slot). */
export const HOLDING_STATUSES: readonly DialysisSessionStatus[] = ['Scheduled', 'InProgress', 'Completed'];

/** True when another session holds this machine in the given shift of the board's day. */
export function isBookedByOther(board: Pick<DialysisBoard, 'machines'> | undefined, machineId: string, shift: DialysisShift, sessionId?: string | null): boolean {
  const machine = board?.machines.find((m) => m.id === machineId);
  return Boolean(machine?.sessions.some((s) => s.id !== sessionId && s.shift === shift && HOLDING_STATUSES.includes(s.status)));
}

export interface MachineChoice {
  id: string;
  code: string;
  station: string | null;
  seroDedication: SeroStatus;
  status: DialysisMachineStatus;
  check: MachineCheck;
}

/** The board day's machines, each checked against the patient's sero status, the shift and the use. */
export function machineChoices(
  board: Pick<DialysisBoard, 'machines'> | undefined,
  sero: SeroStatus | null,
  shift: DialysisShift,
  use: MachineUse,
  sessionId?: string | null,
): MachineChoice[] {
  return (board?.machines ?? []).map((m) => ({
    id: m.id,
    code: m.code,
    station: m.station,
    seroDedication: m.seroDedication,
    status: m.status,
    check: checkMachine(m, sero, use, isBookedByOther(board, m.id, shift, sessionId)),
  }));
}

/** First machine that passes every check, preferring ones that are free and already disinfected. */
export function suggestMachine<T extends MachineLike & { id: string }>(
  machines: readonly T[],
  patientSero: SeroStatus | null,
  shift: DialysisShift,
  board: Pick<DialysisBoard, 'machines'> | undefined,
  sessionId?: string | null,
): T | null {
  const candidates = machines
    .map((machine) => ({ machine, check: checkMachine(machine, patientSero, 'assign', isBookedByOther(board, machine.id, shift, sessionId)) }))
    .filter((c) => c.check.eligible);
  candidates.sort((a, b) => a.check.warnings.length - b.check.warnings.length || a.machine.code.localeCompare(b.machine.code));
  return candidates[0]?.machine ?? null;
}

// ---------------------------------------------------------------- fluid and weight

const round1 = (value: number) => Math.round(value * 10) / 10;

/** Weight gained above dry weight, in kg (null when either is missing). */
export function weightAboveDry(preWeightKg: number | null | undefined, dryWeightKg: number | null | undefined): number | null {
  if (preWeightKg === null || preWeightKg === undefined || dryWeightKg === null || dryWeightKg === undefined) {
    return null;
  }
  return round1(preWeightKg - dryWeightKg);
}

/** A starting point for the ultrafiltration goal: the weight above dry weight (1 kg ≈ 1000 ml). The nurse decides. */
export function suggestedUfMl(preWeightKg: number | null | undefined, dryWeightKg: number | null | undefined): number | null {
  const gain = weightAboveDry(preWeightKg, dryWeightKg);
  return gain === null ? null : Math.max(0, Math.round(gain * 1000));
}

/** Weight lost during the session in ml — what the server records as UF achieved when none is entered. */
export function weightLossMl(preWeightKg: number | null | undefined, postWeightKg: number | null | undefined): number | null {
  if (preWeightKg === null || preWeightKg === undefined || postWeightKg === null || postWeightKg === undefined) {
    return null;
  }
  return Math.round((preWeightKg - postWeightKg) * 1000);
}

// ---------------------------------------------------------------- dialyzers

/** HBsAg-positive patients never take part in dialyzer reuse; otherwise the unit policy applies. */
export function effectiveMaxUses(policy: DialyzerReusePolicy, sero: SeroStatus, maxUses: number): number {
  return policy === 'SingleUse' || sero === 'HBsAg' ? 1 : Math.max(1, maxUses);
}

export interface DialyzerCheck {
  usable: boolean;
  reason: string | null;
}

/** Same checks as `Dialyzer.EnsureUsableFor`: own patient only, not discarded, uses left. */
export function checkDialyzer(dialyzer: Pick<DialyzerDto, 'patientId' | 'serialNumber' | 'isDiscarded' | 'usesCount' | 'maxUses'>, patientId: string): DialyzerCheck {
  if (dialyzer.patientId !== patientId) {
    return { usable: false, reason: `Dialyzer ${dialyzer.serialNumber} belongs to another patient and must never be used for this patient.` };
  }
  if (dialyzer.isDiscarded) {
    return { usable: false, reason: `Dialyzer ${dialyzer.serialNumber} has been discarded.` };
  }
  if (dialyzer.usesCount >= dialyzer.maxUses) {
    return { usable: false, reason: `Dialyzer ${dialyzer.serialNumber} has reached its maximum of ${dialyzer.maxUses} use(s).` };
  }
  return { usable: true, reason: null };
}

/** "Use 4 of 8" — the use number printed on the chart for the next session. */
export function nextUseLabel(dialyzer: Pick<DialyzerDto, 'usesCount' | 'maxUses'>): string {
  return `Use ${dialyzer.usesCount + 1} of ${dialyzer.maxUses}`;
}

// ---------------------------------------------------------------- session lifecycle

export type SessionStage = 'machine' | 'pre-assessment' | 'start' | 'running' | 'completed' | 'closed';

/** Where a session stands: the next thing to do. */
export function sessionStage(detail: Pick<DialysisSessionDetail, 'status' | 'machineId' | 'preAssessedAt'>): SessionStage {
  switch (detail.status) {
    case 'InProgress':
      return 'running';
    case 'Completed':
      return 'completed';
    case 'Cancelled':
    case 'Missed':
      return 'closed';
    default:
      return !detail.machineId ? 'machine' : !detail.preAssessedAt ? 'pre-assessment' : 'start';
  }
}

/** Sero-negative patients are re-screened every 3 months (server rule `SeroSegregation.IsRescreeningDue`). */
export const RESCREENING_DAYS = 90;

export function isRescreeningDue(testedOn: string | null, today: string): boolean {
  if (!testedOn) {
    return true;
  }
  const days = Math.round((Date.parse(`${today}T00:00:00Z`) - Date.parse(`${testedOn}T00:00:00Z`)) / 86_400_000);
  return days > RESCREENING_DAYS;
}
