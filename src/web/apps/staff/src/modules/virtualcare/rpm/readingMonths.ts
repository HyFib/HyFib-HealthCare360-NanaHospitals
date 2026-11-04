// The months a remote monitoring enrolment's home readings can be in, as ABDM wellness records hold them (gap #27).

const IST_MS = 330 * 60 * 1000;

/** The month (yyyy-MM) of an instant in India, where readings are dated. */
export function istMonth(instant: string): string {
  return new Date(Date.parse(instant) + IST_MS).toISOString().slice(0, 7);
}

function nextMonth(month: string): string {
  const [year, number] = month.split('-').map(Number) as [number, number];
  return number === 12 ? `${year + 1}-01` : `${year}-${String(number + 1).padStart(2, '0')}`;
}

function previousMonth(month: string): string {
  const [year, number] = month.split('-').map(Number) as [number, number];
  return number === 1 ? `${year - 1}-12` : `${year}-${String(number - 1).padStart(2, '0')}`;
}

/**
 * The months an enrolment's readings can be in, newest first: from the month before it started (a device sends the
 * readings it stored for up to 30 days, some from before the enrolment) to the month of its latest reading.
 */
export function readingMonths(startDate: string, lastReadingAt: string): string[] {
  const last = istMonth(lastReadingAt);
  const months: string[] = [];
  for (let month = previousMonth(startDate.slice(0, 7)); month <= last; month = nextMonth(month)) {
    months.push(month);
  }
  return months.reverse();
}

/** "September 2026". */
export function monthLabel(month: string): string {
  return new Intl.DateTimeFormat('en-IN', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(
    new Date(`${month}-01T00:00:00Z`),
  );
}
