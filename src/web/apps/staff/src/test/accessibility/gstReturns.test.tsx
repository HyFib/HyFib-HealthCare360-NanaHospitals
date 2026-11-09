import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '../appScreen';
import { GSTR1, ANSWERS as GST_RETURNS } from '../roleAccess/gst-returns';
import { BILLING_ANSWERS } from './fixtures/billing';

// The GST returns screen, whole (shell included) with a canned month, checked with axe-core for WCAG 2.2 A and AA
// problems (SRS UX-10): GSTR-3B, the GSTR-1 tables, the HSN summary and the documents issued.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  server = fakeServer({ ...BILLING_ANSWERS, ...GST_RETURNS });
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('GST returns', SCREEN_TESTS, () => {
  it("a month's GSTR-3B and GSTR-1", async () => {
    openScreen('/billing/gst-returns?month=2026-10');
    expect(await screen.findByText('NHIB26000001', {}, SLOW)).toBeInTheDocument();
    expect(await screen.findByText(/3.1\(a\) Outward taxable supplies/, {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('NHOB26000001 – NHOB26000002')).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('a month whose filing is recorded, with a late paper bill to amend', async () => {
    server = fakeServer({
      ...BILLING_ANSWERS,
      ...GST_RETURNS,
      'GET /api/v1/billing/gst-returns/gstr1': {
        ...GSTR1,
        filing: {
          year: 2026,
          month: 10,
          arn: 'AA331026000001Z',
          filedOn: '2026-11-10',
          recordedAt: '2026-11-10T06:30:00Z',
          recordedByName: 'Accounts',
          correctedAt: null,
          correctedByName: null,
          correctionReason: null,
          version: 1,
        },
        lateEntries: [
          {
            number: 'NHOB26000009',
            date: '2026-09-30',
            value: 1180,
            filedYear: 2026,
            filedMonth: 9,
            filedArn: 'AA330926000001Z',
            enteredOn: '2026-10-02',
          },
        ],
      },
    });
    openScreen('/billing/gst-returns?month=2026-10');
    expect(await screen.findByText(/ARN AA331026000001Z/, {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('NHOB26000009')).toBeInTheDocument();
    expect(screen.getByText(/AA330926000001Z/)).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});
