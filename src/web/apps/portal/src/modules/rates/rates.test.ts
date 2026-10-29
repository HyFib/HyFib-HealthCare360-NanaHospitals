import { describe, expect, it } from 'vitest';
import { createTranslator } from '@/i18n';
import { formattersFor } from '@/i18n/useFormat';
import { RATE_LIST } from '@/test/fixtures';
import { filterRateList, isEmptyRateList, rateNotes } from './model';
import { rateListDocument } from './print';

const rates = RATE_LIST.rates;
const percent = (value: number) => `${value}%`;

describe('finding a service in the rate list', () => {
  it('keeps the services whose English name, local name or code matches, and drops the rest', () => {
    expect(filterRateList(rates, 'DRESSING').sections).toEqual([
      { category: 'Procedure', lines: [rates.sections[1]!.lines[0]] },
    ]);
    expect(filterRateList(rates, 'இரத்த').sections.map((s) => s.lines.map((l) => l.code))).toEqual([['LAB-CBC']]);
    expect(filterRateList(rates, 'prc-imp').sections.map((s) => s.lines.map((l) => l.code))).toEqual([['PRC-IMP']]);
  });

  it('finds a doctor by name or department, and a room by a ward', () => {
    const doctor = filterRateList(rates, 'general medicine');
    expect(doctor.doctors.map((d) => d.name)).toEqual(['Dr. Meera Iyer']);
    expect(doctor.sections).toEqual([]);
    expect(doctor.registration).toBeNull();
    expect(filterRateList(rates, '3a').rooms.map((r) => r.day.code)).toEqual(['BED-PVT']);
  });

  it('is the whole list without a term, and empty when nothing matches', () => {
    expect(filterRateList(rates, '  ')).toBe(rates);
    expect(isEmptyRateList(filterRateList(rates, 'heart transplant'))).toBe(true);
    expect(isEmptyRateList(rates)).toBe(false);
  });
});

describe('the notes under the rate list', () => {
  it('are the printed list’s: whose rates, GST, a price that may vary when one may, and MRP', () => {
    expect(rateNotes(rates, percent).map((n) => n.key)).toEqual([
      'rates.note.selfPay',
      'rates.note.gst',
      'rates.note.mayVary',
      'rates.note.medicines',
    ]);
  });

  it('name the concession when there is one, and leave out "may vary" when no price may', () => {
    const fixed = {
      ...rates,
      concessionPercent: 10,
      sections: rates.sections.map((s) => ({ ...s, lines: s.lines.filter((l) => !l.mayVary) })),
    };
    expect(rateNotes(fixed, percent)).toEqual([
      { key: 'rates.note.selfPay' },
      { key: 'rates.note.concession', vars: { percent: '10%' } },
      { key: 'rates.note.gst' },
      { key: 'rates.note.medicines' },
    ]);
  });
});

describe('the printed copy of the rate list', () => {
  const doc = (language: 'en' | 'ta') =>
    rateListDocument(RATE_LIST, {
      t: createTranslator(language),
      f: formattersFor(language),
      printedAt: '2026-10-29T10:00:00+05:30',
    });

  it('marks each local name with its language, so it is read and shown as Tamil', () => {
    expect(doc('en').body).toContain('<span class="local" lang="ta">சிறிய காயக் கட்டு</span>');
  });

  it('prints the amounts with GST, the facility and the day, in the reader’s language', () => {
    const english = doc('en');
    expect(english.body).toContain('Nana Hospitals');
    expect(english.body).toContain('₹531.00');
    expect(english.body).toContain('with 18% GST');
    expect(english.fileName).toBe('rate-list-2026-10-29');
    expect(doc('ta').body).toContain('சேவைக் கட்டணப் பட்டியல்');
  });

  it('prints a service the counter prices by what is used as the counter’s list does, not as nothing to pay', () => {
    const english = doc('en').body;
    expect(english).toContain('By what is used');
    expect(english).not.toContain('₹0.00');
    expect(doc('ta').body).toContain('பயன்பாட்டின்படி');
  });

  it('escapes what the hospital typed', () => {
    const typed = {
      ...RATE_LIST,
      rates: {
        ...rates,
        sections: [
          {
            category: 'Procedure' as const,
            lines: [{ ...rates.sections[1]!.lines[0]!, name: '<img src=x onerror=alert(1)>' }],
          },
        ],
      },
    };
    const body = rateListDocument(typed, {
      t: createTranslator('en'),
      f: formattersFor('en'),
      printedAt: '2026-10-29T10:00:00+05:30',
    }).body;
    expect(body).not.toContain('<img');
    expect(body).toContain('&lt;img src=x onerror=alert(1)&gt;');
  });
});
