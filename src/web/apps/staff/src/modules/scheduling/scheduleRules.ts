import { WEEKDAYS, type ScheduleDto, type Weekday } from './api';
import { timeToMinutes, type IsoDate } from './dates';

/** Mirrors `DoctorSchedule` limits on the server. */
export const MIN_SLOT_MINUTES = 5;
export const MAX_SLOT_MINUTES = 120;
export const MAX_CAPACITY_PER_SLOT = 20;

/** Slot lengths offered in the editor (any value from 5 to 120 is valid on the server). */
export const SLOT_LENGTHS = [5, 10, 15, 20, 30, 45, 60, 90, 120] as const;

export const WEEKDAY_PRESETS: { label: string; days: Weekday[] }[] = [
  { label: 'Mon–Fri', days: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'] },
  { label: 'Mon–Sat', days: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'] },
  { label: 'Every day', days: [...WEEKDAYS] },
];

/** Whole slots that fit in a session (a trailing remainder shorter than a slot is not bookable). */
export function slotsPerSession(startTime: string, endTime: string, slotMinutes: number): number {
  const start = timeToMinutes(startTime);
  const end = timeToMinutes(endTime);
  if (Number.isNaN(start) || Number.isNaN(end) || !(slotMinutes > 0) || end <= start) {
    return 0;
  }
  return Math.floor((end - start) / slotMinutes);
}

export interface FieldProblem {
  field: 'startTime' | 'endTime' | 'slotMinutes' | 'capacityPerSlot' | 'effectiveTo';
  message: string;
}

/** The server's `DoctorSchedule.Validate`, so the editor shows the same message on the same field before saving. */
export function sessionProblem(input: {
  startTime: string;
  endTime: string;
  slotMinutes: number;
  capacityPerSlot: number;
  effectiveFrom?: IsoDate | null;
  effectiveTo?: IsoDate | null;
}): FieldProblem | null {
  const start = timeToMinutes(input.startTime);
  const end = timeToMinutes(input.endTime);
  if (Number.isNaN(start)) {
    return { field: 'startTime', message: 'Enter the start time.' };
  }
  if (Number.isNaN(end)) {
    return { field: 'endTime', message: 'Enter the end time.' };
  }
  if (
    !Number.isInteger(input.slotMinutes) ||
    input.slotMinutes < MIN_SLOT_MINUTES ||
    input.slotMinutes > MAX_SLOT_MINUTES
  ) {
    return {
      field: 'slotMinutes',
      message: `Slot length must be between ${MIN_SLOT_MINUTES} and ${MAX_SLOT_MINUTES} minutes.`,
    };
  }
  if (end <= start) {
    return { field: 'endTime', message: 'The session must end after it starts.' };
  }
  if (end - start < input.slotMinutes) {
    return { field: 'endTime', message: 'The session is shorter than one slot.' };
  }
  if (
    !Number.isInteger(input.capacityPerSlot) ||
    input.capacityPerSlot < 1 ||
    input.capacityPerSlot > MAX_CAPACITY_PER_SLOT
  ) {
    return { field: 'capacityPerSlot', message: `Patients per slot must be between 1 and ${MAX_CAPACITY_PER_SLOT}.` };
  }
  if (input.effectiveFrom && input.effectiveTo && input.effectiveTo < input.effectiveFrom) {
    return { field: 'effectiveTo', message: 'The end date must be on or after the start date.' };
  }
  return null;
}

export interface SessionShape {
  id?: string;
  practitionerId: string;
  weekday: Weekday;
  startTime: string;
  endTime: string;
  effectiveFrom: IsoDate;
  effectiveTo: IsoDate | null;
  isActive: boolean;
}

/**
 * True when two sessions of the same doctor could produce slots at the same time on some date — the server rejects
 * such a change (409 `schedule.overlap`), so the editor warns before saving. Mirrors `DoctorSchedule.Overlaps`.
 */
export function sessionsOverlap(a: SessionShape, b: SessionShape): boolean {
  if (
    (a.id && a.id === b.id) ||
    !a.isActive ||
    !b.isActive ||
    a.practitionerId !== b.practitionerId ||
    a.weekday !== b.weekday
  ) {
    return false;
  }
  const aStart = timeToMinutes(a.startTime);
  const aEnd = timeToMinutes(a.endTime);
  const bStart = timeToMinutes(b.startTime);
  const bEnd = timeToMinutes(b.endTime);
  if ([aStart, aEnd, bStart, bEnd].some(Number.isNaN) || !(aStart < bEnd && bStart < aEnd)) {
    return false;
  }
  const far = '9999-12-31';
  return a.effectiveFrom <= (b.effectiveTo ?? far) && b.effectiveFrom <= (a.effectiveTo ?? far);
}

/** Existing sessions a proposed session would clash with (one entry per clashing weekday). */
export function findOverlaps(
  proposal: Omit<SessionShape, 'weekday'> & { weekdays: Weekday[] },
  existing: readonly ScheduleDto[],
): ScheduleDto[] {
  return existing.filter((schedule) =>
    proposal.weekdays.some((weekday) =>
      sessionsOverlap(
        { ...proposal, weekday },
        {
          id: schedule.id,
          practitionerId: schedule.practitionerId,
          weekday: schedule.weekday,
          startTime: schedule.startTime,
          endTime: schedule.endTime,
          effectiveFrom: schedule.effectiveFrom,
          effectiveTo: schedule.effectiveTo,
          isActive: schedule.isActive,
        },
      ),
    ),
  );
}

/** Sessions ordered Monday to Sunday, then by start time. */
export function sortSessions<T extends { weekday: Weekday; startTime: string }>(sessions: readonly T[]): T[] {
  return [...sessions].sort(
    (a, b) =>
      WEEKDAYS.indexOf(a.weekday) - WEEKDAYS.indexOf(b.weekday) ||
      timeToMinutes(a.startTime) - timeToMinutes(b.startTime),
  );
}
