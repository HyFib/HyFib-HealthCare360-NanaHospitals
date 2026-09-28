/**
 * Layout maths for the appointment calendar (pure, unit-tested). Times are minutes after IST midnight.
 */

export interface TimedItem {
  id: string;
  start: number;
  end: number;
}

export interface LanePlacement {
  /** Zero-based column inside the overlap group. */
  lane: number;
  /** Columns in the overlap group (the item's width is 1 / lanes). */
  lanes: number;
}

/**
 * Places overlapping items side by side, like a day view in Outlook: items that overlap (directly or through a chain)
 * form a group; each takes the first column that is free when it starts, and every item of the group gets the
 * group's column count so blocks line up.
 */
export function assignLanes(items: readonly TimedItem[]): Map<string, LanePlacement> {
  const sorted = [...items].sort((a, b) => a.start - b.start || b.end - a.end || a.id.localeCompare(b.id));
  const placements = new Map<string, LanePlacement>();
  let group: { id: string; lane: number }[] = [];
  let laneEnds: number[] = [];
  let groupEnd = Number.NEGATIVE_INFINITY;

  const closeGroup = () => {
    const lanes = Math.max(1, laneEnds.length);
    for (const member of group) {
      placements.set(member.id, { lane: member.lane, lanes });
    }
    group = [];
    laneEnds = [];
    groupEnd = Number.NEGATIVE_INFINITY;
  };

  for (const item of sorted) {
    const end = Math.max(item.end, item.start + 1);
    if (item.start >= groupEnd) {
      closeGroup();
    }
    let lane = laneEnds.findIndex((laneEnd) => laneEnd <= item.start);
    if (lane === -1) {
      lane = laneEnds.length;
      laneEnds.push(end);
    } else {
      laneEnds[lane] = end;
    }
    group.push({ id: item.id, lane });
    groupEnd = Math.max(groupEnd, end);
  }
  closeGroup();
  return placements;
}

export interface MinuteRange {
  start: number;
  end: number;
}

/** Whole hours covering every span (sessions and appointments); a default working day when there is nothing. */
export function visibleRange(spans: readonly MinuteRange[], fallback: MinuteRange = { start: 8 * 60, end: 20 * 60 }): MinuteRange {
  if (spans.length === 0) {
    return fallback;
  }
  const start = Math.max(0, Math.floor(Math.min(...spans.map((s) => s.start)) / 60) * 60);
  const end = Math.min(24 * 60, Math.ceil(Math.max(...spans.map((s) => s.end)) / 60) * 60);
  return { start, end: Math.max(end, start + 60) };
}

/** Minutes at every full hour from the start of the range up to (not including) its end. */
export function hourMarks(range: MinuteRange): number[] {
  const marks: number[] = [];
  for (let minute = Math.ceil(range.start / 60) * 60; minute < range.end; minute += 60) {
    marks.push(minute);
  }
  return marks;
}

export interface Utilisation {
  capacity: number;
  booked: number;
  /** 0–100, rounded; above 100 when overbooked. */
  percent: number;
}

/** Booked seats against capacity over a set of slots. */
export function utilisation(slots: readonly { capacity: number; booked: number }[]): Utilisation {
  const capacity = slots.reduce((sum, slot) => sum + slot.capacity, 0);
  const booked = slots.reduce((sum, slot) => sum + slot.booked, 0);
  return { capacity, booked, percent: capacity > 0 ? Math.round((booked / capacity) * 100) : 0 };
}
