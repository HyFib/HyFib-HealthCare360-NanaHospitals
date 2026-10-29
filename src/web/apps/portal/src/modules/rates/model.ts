import type { MessageKey, MessageVars } from '@/i18n';
import type { RateLine, RateList, ServiceCategory } from '@/lib/types';

// Lower case, in one Unicode form: "X-Ray" finds "x-ray", and Tamil typed on a phone finds the name as entered.
function normal(text: string | null | undefined): string {
  return (text ?? '').normalize('NFC').toLocaleLowerCase('en-IN').trim();
}

function lineMatches(line: RateLine, wanted: string): boolean {
  return [line.name, line.localName, line.code].some((value) => normal(value).includes(wanted));
}

/**
 * The list with only what matches `term`: a service by its English or local name or its code, a doctor by name or
 * department, a room by its name or a ward. The whole list when there is no term.
 */
export function filterRateList(rates: RateList, term: string): RateList {
  const wanted = normal(term);
  if (!wanted) {
    return rates;
  }
  return {
    ...rates,
    registration: rates.registration && lineMatches(rates.registration, wanted) ? rates.registration : null,
    doctors: rates.doctors.filter((d) => normal(d.name).includes(wanted) || normal(d.department).includes(wanted)),
    rooms: rates.rooms.filter((r) => lineMatches(r.day, wanted) || r.wards.some((w) => normal(w).includes(wanted))),
    sections: rates.sections
      .map((section) => ({ ...section, lines: section.lines.filter((line) => lineMatches(line, wanted)) }))
      .filter((section) => section.lines.length > 0),
    packages: rates.packages.filter((p) => lineMatches(p.price, wanted)),
  };
}

/** Whether the list has nothing in it. */
export function isEmptyRateList(rates: RateList): boolean {
  return (
    rates.registration === null &&
    rates.doctors.length === 0 &&
    rates.rooms.length === 0 &&
    rates.sections.length === 0 &&
    rates.packages.length === 0
  );
}

/** Every priced line of the list. */
export function rateLines(rates: RateList): RateLine[] {
  return [
    ...(rates.registration ? [rates.registration] : []),
    ...rates.doctors.flatMap((d) => (d.video ? [d.fee, d.video] : [d.fee])),
    ...rates.rooms.flatMap((r) => [r.day, ...(r.nursing ? [r.nursing] : []), ...(r.visit ? [r.visit] : [])]),
    ...rates.sections.flatMap((s) => s.lines),
    ...rates.packages.map((p) => p.price),
  ];
}

/** A category's heading, as the printed list words it. */
export function categoryKey(category: ServiceCategory): MessageKey {
  return `rates.category.${category}`;
}

export interface RateNote {
  key: MessageKey;
  vars?: MessageVars;
}

/**
 * The notes under the list, as the printed list has them: whose rates these are, the concession they include, where GST
 * is charged, that the counter may price some services by what is used (when any may), and the MRP of medicines.
 * `percent` writes a percentage in the reader's language.
 */
export function rateNotes(rates: RateList, percent: (value: number) => string): RateNote[] {
  return [
    { key: 'rates.note.selfPay' },
    ...(rates.concessionPercent > 0
      ? [{ key: 'rates.note.concession' as const, vars: { percent: percent(rates.concessionPercent) } }]
      : []),
    { key: 'rates.note.gst' },
    ...(rateLines(rates).some((line) => line.mayVary) ? [{ key: 'rates.note.mayVary' as const }] : []),
    { key: 'rates.note.medicines' },
  ];
}
