import { describe, expect, it } from 'vitest';
import type { ScorecardRow } from './api';
import { scorecardHtml } from './print';

const row = (partial: Partial<ScorecardRow>): ScorecardRow => ({
  indicatorId: 'i',
  code: 'HH',
  name: 'Hand hygiene compliance',
  category: 'Infection control',
  unit: 'Percent',
  target: 90,
  direction: 'HigherIsBetter',
  periodStart: '2026-08-01',
  periodLabel: 'Aug 2026',
  value: 92.5,
  meetsTarget: true,
  ...partial,
});

describe('scorecard print', () => {
  it('groups by category, counts results and escapes values', () => {
    const html = scorecardHtml(
      [row({}), row({ code: 'FALL', name: 'Falls <per 1000>', category: 'Patient safety', unit: 'PerThousand', target: 1, direction: 'LowerIsBetter', value: 1.4, meetsTarget: false }), row({ code: 'X', value: null, meetsTarget: null })],
      'Nana & Sons Hospital',
      'August 2026',
      '2026-09-01T04:30:00Z',
    );
    expect(html).toContain('Nana &amp; Sons Hospital');
    expect(html).toContain('Falls &lt;per 1000&gt;');
    expect(html).toContain('1 met · 1 missed · 1 without data');
    expect(html).toContain('92.5%');
    expect(html).toContain('1.4 per 1,000');
    expect(html.indexOf('Infection control')).toBeLessThan(html.indexOf('Patient safety'));
  });
});
