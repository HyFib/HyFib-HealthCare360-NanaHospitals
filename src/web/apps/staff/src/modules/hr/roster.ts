/** Pure roster-grid logic (painting cells, working out what to save or delete, day coverage). */
import type { RosterAssignmentInput, RosterConflictDto, RosterEntryDto } from './api';

/** One duty in a cell: a shift or a week off. Two shifts in one cell are a double duty. */
export interface CellItem {
  shiftId: string | null;
  isWeekOff: boolean;
  notes: string | null;
}

/** Cells keyed by `employeeId|date`. */
export type Cells = Record<string, CellItem[]>;

export type Brush = { kind: 'shift'; shiftId: string } | { kind: 'weekOff' } | { kind: 'clear' };

/** At most this many duties in one cell (a double duty plus one more is already exceptional). */
export const MAX_DUTIES_PER_DAY = 3;

export function cellKey(employeeId: string, date: string): string {
  return `${employeeId}|${date}`;
}

export function splitCellKey(key: string): { employeeId: string; date: string } {
  const [employeeId = '', date = ''] = key.split('|');
  return { employeeId, date };
}

/** Groups saved roster entries by cell, shifts in start-time order. */
export function cellsFromEntries(entries: RosterEntryDto[]): Cells {
  const sorted = [...entries].sort((a, b) => (a.startTime ?? '99').localeCompare(b.startTime ?? '99'));
  const cells: Cells = {};
  for (const entry of sorted) {
    const key = cellKey(entry.employeeId, entry.date);
    (cells[key] ??= []).push({ shiftId: entry.shiftId, isWeekOff: entry.isWeekOff, notes: entry.notes });
  }
  return cells;
}

/** Saved entry ids per cell (needed to delete a cell the planner cleared). */
export function entryIdsByCell(entries: RosterEntryDto[]): Record<string, string[]> {
  const ids: Record<string, string[]> = {};
  for (const entry of entries) {
    (ids[cellKey(entry.employeeId, entry.date)] ??= []).push(entry.id);
  }
  return ids;
}

function itemKey(item: CellItem): string {
  return item.isWeekOff ? 'off' : `shift:${item.shiftId ?? ''}`;
}

/** Same duties in a cell, ignoring order and notes. */
export function sameItems(a: CellItem[], b: CellItem[]): boolean {
  if (a.length !== b.length) {
    return false;
  }
  const left = a.map(itemKey).sort();
  const right = b.map(itemKey).sort();
  return left.every((key, index) => key === right[index]);
}

/**
 * Applies the selected brush to a cell. A shift replaces what is there, or with `append` (Shift+click) is added as a
 * double duty. A week off always stands alone.
 */
export function paint(current: CellItem[], brush: Brush, append = false): CellItem[] {
  switch (brush.kind) {
    case 'clear':
      return [];
    case 'weekOff':
      return [{ shiftId: null, isWeekOff: true, notes: null }];
    case 'shift': {
      const item: CellItem = { shiftId: brush.shiftId, isWeekOff: false, notes: null };
      const shifts = current.filter((existing) => !existing.isWeekOff);
      if (append && shifts.length > 0) {
        if (shifts.some((existing) => existing.shiftId === brush.shiftId) || shifts.length >= MAX_DUTIES_PER_DAY) {
          return shifts;
        }
        return [...shifts, item];
      }
      if (shifts.length === 1 && current.length === 1 && shifts[0]?.shiftId === brush.shiftId) {
        return current;
      }
      return [item];
    }
  }
}

export interface RosterDiff {
  /** Cells to send to PUT /roster (they replace whatever is saved for those employee-days). */
  assignments: RosterAssignmentInput[];
  /** Cells emptied by the planner: their saved entries are deleted. */
  clearedKeys: string[];
  changedKeys: string[];
}

/** Compares the planner's edits with the saved roster. */
export function diffRoster(saved: Cells, draft: Cells): RosterDiff {
  const assignments: RosterAssignmentInput[] = [];
  const clearedKeys: string[] = [];
  const changedKeys: string[] = [];
  for (const [key, items] of Object.entries(draft)) {
    const before = saved[key] ?? [];
    if (sameItems(before, items)) {
      continue;
    }
    changedKeys.push(key);
    const { employeeId, date } = splitCellKey(key);
    if (items.length === 0) {
      if (before.length > 0) {
        clearedKeys.push(key);
      }
      continue;
    }
    for (const item of items) {
      assignments.push({ employeeId, date, shiftId: item.isWeekOff ? null : item.shiftId, isWeekOff: item.isWeekOff, notes: item.notes });
    }
  }
  return { assignments, clearedKeys, changedKeys };
}

export function conflictsByCell(conflicts: RosterConflictDto[]): Record<string, RosterConflictDto[]> {
  const byCell: Record<string, RosterConflictDto[]> = {};
  for (const conflict of conflicts) {
    (byCell[cellKey(conflict.employeeId, conflict.date)] ??= []).push(conflict);
  }
  return byCell;
}

/** Staff on each shift per day (and week offs under the key "off"), for the coverage row under the grid. */
export function coverage(cells: Cells, dates: string[]): Record<string, Record<string, number>> {
  const result: Record<string, Record<string, number>> = Object.fromEntries(dates.map((date) => [date, {}]));
  for (const [key, items] of Object.entries(cells)) {
    const { date } = splitCellKey(key);
    const day = result[date];
    if (!day) {
      continue;
    }
    for (const item of items) {
      const bucket = item.isWeekOff ? 'off' : (item.shiftId ?? 'unknown');
      day[bucket] = (day[bucket] ?? 0) + 1;
    }
  }
  return result;
}

/** Merges the saved roster with the planner's edits (edits win). */
export function mergeCells(saved: Cells, draft: Cells): Cells {
  return { ...saved, ...draft };
}
