import { describe, expect, it } from 'vitest';
import type { RosterEntryDto } from './api';
import {
  cellKey,
  cellsFromEntries,
  conflictsByCell,
  coverage,
  diffRoster,
  entryIdsByCell,
  paint,
  sameItems,
  type CellItem,
} from './roster';

const MOR: CellItem = { shiftId: 'mor', isWeekOff: false, notes: null };
const EVE: CellItem = { shiftId: 'eve', isWeekOff: false, notes: null };
const OFF: CellItem = { shiftId: null, isWeekOff: true, notes: null };

function entry(
  id: string,
  employeeId: string,
  date: string,
  shiftId: string | null,
  startTime: string | null,
): RosterEntryDto {
  return {
    id,
    employeeId,
    employeeCode: 'E',
    employeeName: 'Name',
    date,
    shiftId,
    shiftCode: shiftId?.toUpperCase() ?? null,
    shiftName: null,
    color: null,
    startTime,
    endTime: null,
    isWeekOff: shiftId === null,
    notes: null,
  };
}

describe('roster cells', () => {
  it('groups saved entries by employee and day in shift order', () => {
    const entries = [
      entry('2', 'a', '2026-09-21', 'eve', '14:00:00'),
      entry('1', 'a', '2026-09-21', 'mor', '07:00:00'),
      entry('3', 'b', '2026-09-21', null, null),
    ];
    const cells = cellsFromEntries(entries);
    expect(cells[cellKey('a', '2026-09-21')]?.map((c) => c.shiftId)).toEqual(['mor', 'eve']);
    expect(cells[cellKey('b', '2026-09-21')]?.[0]?.isWeekOff).toBe(true);
    expect(entryIdsByCell(entries)[cellKey('a', '2026-09-21')]).toEqual(['2', '1']);
  });

  it('compares duties regardless of order', () => {
    expect(sameItems([MOR, EVE], [EVE, MOR])).toBe(true);
    expect(sameItems([MOR], [EVE])).toBe(false);
    expect(sameItems([], [])).toBe(true);
  });

  it('paints shifts, week offs and double duties', () => {
    expect(paint([], { kind: 'shift', shiftId: 'mor' })).toEqual([MOR]);
    expect(paint([EVE], { kind: 'shift', shiftId: 'mor' })).toEqual([MOR]);
    expect(paint([MOR], { kind: 'shift', shiftId: 'eve' }, true)).toEqual([MOR, EVE]);
    expect(paint([MOR], { kind: 'shift', shiftId: 'mor' }, true)).toEqual([MOR]);
    expect(paint([OFF], { kind: 'shift', shiftId: 'mor' }, true)).toEqual([MOR]);
    expect(paint([MOR, EVE], { kind: 'weekOff' })).toEqual([OFF]);
    expect(paint([MOR], { kind: 'clear' })).toEqual([]);
  });
});

describe('saving the roster', () => {
  it('sends only changed cells and deletes emptied ones', () => {
    const saved = { [cellKey('a', '2026-09-21')]: [MOR], [cellKey('b', '2026-09-21')]: [EVE] };
    const draft = {
      [cellKey('a', '2026-09-21')]: [MOR], // unchanged
      [cellKey('b', '2026-09-21')]: [], // cleared
      [cellKey('c', '2026-09-22')]: [MOR, EVE], // new double duty
      [cellKey('d', '2026-09-22')]: [], // empty before and after
    };
    const diff = diffRoster(saved, draft);
    expect(diff.changedKeys).toEqual([cellKey('b', '2026-09-21'), cellKey('c', '2026-09-22')]);
    expect(diff.clearedKeys).toEqual([cellKey('b', '2026-09-21')]);
    expect(diff.assignments).toEqual([
      { employeeId: 'c', date: '2026-09-22', shiftId: 'mor', isWeekOff: false, notes: null },
      { employeeId: 'c', date: '2026-09-22', shiftId: 'eve', isWeekOff: false, notes: null },
    ]);
  });

  it('maps conflicts to cells and counts coverage per day', () => {
    const byCell = conflictsByCell([
      {
        employeeId: 'a',
        employeeName: 'A',
        date: '2026-09-21',
        kind: 'OnLeave',
        severity: 'Warning',
        message: 'On leave',
      },
    ]);
    expect(byCell[cellKey('a', '2026-09-21')]).toHaveLength(1);
    const counts = coverage(
      {
        [cellKey('a', '2026-09-21')]: [MOR],
        [cellKey('b', '2026-09-21')]: [MOR, EVE],
        [cellKey('c', '2026-09-21')]: [OFF],
      },
      ['2026-09-21', '2026-09-22'],
    );
    expect(counts['2026-09-21']).toEqual({ mor: 2, eve: 1, off: 1 });
    expect(counts['2026-09-22']).toEqual({});
  });
});
