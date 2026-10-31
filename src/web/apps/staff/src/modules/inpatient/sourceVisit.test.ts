import { describe, expect, it } from 'vitest';
import { sourceVisitChoices } from './sourceVisit';

const NOW = Date.parse('2026-10-31T11:00:00Z');
const hoursAgo = (h: number) => new Date(NOW - h * 3_600_000).toISOString();
const visit = (id: string, type: string, visitedAt: string, status = 'InProgress') =>
  ({ id, type, status, visitedAt }) as Parameters<typeof sourceVisitChoices>[0][number];

describe('the visit a stay comes from', () => {
  // As in the production-style run: she was seen in OPD at 3:19 pm and admitted at 4 pm, "Advised in OPD".
  const visits = [
    visit('opd-today', 'OutPatient', hoursAgo(1)),
    visit('er-yesterday', 'Emergency', hoursAgo(20)),
    visit('opd-last-week', 'OutPatient', hoursAgo(24 * 7)),
    visit('opd-cancelled', 'OutPatient', hoursAgo(2), 'Cancelled'),
    visit('opd-two-days', 'OutPatient', hoursAgo(48)),
  ];

  it('offers the out-patient visits of the last three days for an admission advised in OPD, latest first', () => {
    expect(sourceVisitChoices(visits, 'OutPatient', NOW).map((v) => v.id)).toEqual(['opd-today', 'opd-two-days']);
  });

  it('offers the casualty visits for an admission from the ER', () => {
    expect(sourceVisitChoices(visits, 'Emergency', NOW).map((v) => v.id)).toEqual(['er-yesterday']);
  });

  it('offers none for a referral or a direct admission', () => {
    expect(sourceVisitChoices(visits, 'Referral', NOW)).toEqual([]);
    expect(sourceVisitChoices(visits, 'Direct', NOW)).toEqual([]);
  });
});
