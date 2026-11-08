import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis, type FakeRequest } from '@/test/appScreen';
import type { EqasRoundDto, EqasSummary } from './eqas';

// The external quality assessment register (NABL 112, ISO 15189): an unacceptable round shows its z-score and asks for
// its corrective action, which the lab links by the number the CAPA register gave it; the screen passes axe-core.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const ROUND: EqasRoundDto = {
  id: '0192a000-0000-7000-8000-00000000e101',
  schemeId: '0192a000-0000-7000-8000-00000000e001',
  schemeName: 'Clinical chemistry monthly',
  provider: 'CMC Vellore',
  labTestId: '0192a000-0000-7000-8000-00000000e002',
  testCode: 'CREA',
  testName: 'Creatinine',
  unit: 'mg/dL',
  roundCode: '2026/10 sample 2',
  dueDate: '2026-10-28',
  sampleReceivedOn: '2026-10-20',
  reportedValue: 1.62,
  reportedOn: '2026-10-25',
  assignedValue: 1.2,
  assignedSd: 0.1,
  peerGroup: 'Jaffe, kinetic',
  evaluatedOn: '2026-11-05',
  zScore: 4.2,
  performance: 'Unacceptable',
  isOverdue: false,
  needsCapa: true,
  capaId: null,
  capaNumber: null,
  capaStatus: null,
  acceptedAt: null,
  acceptedByName: null,
  acceptanceNote: null,
  version: 3,
};

const SUMMARY: EqasSummary = {
  overdue: 0,
  dueThisWeek: 1,
  needCapa: 1,
  acceptable: 11,
  warning: 1,
  unacceptable: 1,
  attention: [ROUND],
};

let server: ReturnType<typeof fakeServer>;
let linked: unknown = null;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  linked = null;
  server = fakeServer({
    'GET /api/v1/lab/eqas/rounds': paged([ROUND]),
    'GET /api/v1/lab/eqas/summary': SUMMARY,
    'GET /api/v1/lab/eqas/schemes': [],
    [`PUT /api/v1/lab/eqas/rounds/${ROUND.id}/capa`]: (request: FakeRequest) => {
      linked = request.body;
      return { ...ROUND, needsCapa: false, capaNumber: 'NHPCAPA260012', capaStatus: 'Open', version: 4 };
    },
  });
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('EQAS register', SCREEN_TESTS, () => {
  it('shows an unacceptable round and links its corrective action by number', async () => {
    openScreen('/lab/eqas');
    const table = await screen.findByRole('table', { name: 'EQAS rounds' }, SLOW);
    expect(within(table).getByText('4.20 Unacceptable')).toBeInTheDocument();
    expect(within(table).getByText('Needed')).toBeInTheDocument();
    await expectNoAxeViolations();

    await userEvent.click(within(table).getByRole('button', { name: 'Link CAPA' }));
    const dialog = await screen.findByRole('dialog', {}, SLOW);
    await userEvent.type(within(dialog).getByRole('textbox', { name: /CAPA number/ }), 'NHPCAPA260012');
    await expectNoAxeViolations();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(linked).toEqual({ capaId: null, capaNumber: 'NHPCAPA260012', version: 3 }));
  });
});
