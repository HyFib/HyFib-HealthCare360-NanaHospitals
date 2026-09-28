import { HOSPITAL_TIME_ZONE } from '@hc360/core';

const isoDate = new Intl.DateTimeFormat('en-CA', { timeZone: HOSPITAL_TIME_ZONE, year: 'numeric', month: '2-digit', day: '2-digit' });

/** The hospital (IST) calendar date of an instant, as "yyyy-MM-dd" (for date inputs and DateOnly filters). */
export function istDateOf(iso: string | null | undefined): string | undefined {
  return iso ? isoDate.format(new Date(iso)) : undefined;
}
