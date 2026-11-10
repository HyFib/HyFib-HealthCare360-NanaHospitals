import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type * as Router from '@tanstack/react-router';
import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ApprovalRequestDto } from './api';
import { PendingApprovals } from './PendingApprovals';

// The server is outside the test: what is tested is that a record's "waiting for approval" note goes once the request is
// rejected, withdrawn or lapses on another workstation (none of which tells this page), without the page being opened
// again.
const get = vi.hoisted(() => vi.fn());
vi.mock('@/lib/api', () => ({ api: { get } }));
vi.mock('@tanstack/react-router', async (importOriginal) => ({
  ...(await importOriginal<typeof Router>()),
  useNavigate: () => vi.fn(),
}));

const asked: ApprovalRequestDto = {
  id: '0192a000-0000-7000-8000-00000000a905',
  kind: 'diagnostics.lab_amendment',
  title: 'Lab report correction',
  summary: 'Correction of lab report NHLB260000045',
  details: [{ label: 'Potassium', value: '4.2 mmol/L -> 5.8 mmol/L' }],
  reason: 'Wrong tube read',
  status: 'Pending',
  isExpired: false,
  subjectType: 'LabReport',
  subjectId: 'rep1',
  patientId: '0192a000-0000-7000-8000-000000000001',
  requestedBy: '0192a000-0000-7000-8000-000000000ca5',
  requestedByName: 'Dr. Meera Nair',
  requestedAt: '2026-11-01T06:00:00Z',
  expiresAt: '2026-11-08T06:00:00Z',
  decidedBy: null,
  decidedByName: null,
  decidedAt: null,
  decisionNote: null,
  outcome: null,
  canDecide: false,
  canWithdraw: true,
  version: 3,
};

const page = (items: ApprovalRequestDto[]) => ({ items, page: 1, pageSize: 25, totalCount: items.length });

/** Moves the clock `ms` on, letting the queries fetch and the page render what they answered. */
async function after(ms = 0) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

function renderNote() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 30_000 } } })}>
      <FluentProvider theme={webLightTheme}>
        <PendingApprovals subjectType="LabReport" subjectId="rep1" />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('the waiting-for-approval note on a record', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    get.mockReset();
  });
  afterEach(() => vi.useRealTimers());

  it('goes at the next 30-second check once the request is rejected, withdrawn or lapses on another workstation', async () => {
    get.mockResolvedValueOnce(page([asked])).mockResolvedValue(page([]));
    renderNote();
    await after();
    expect(screen.getByText('Waiting for approval')).toBeInTheDocument();

    // The next check, 30 seconds on, finds it no longer waiting; the page shows that a moment later.
    await after(30_000);
    await after(1_000);
    expect(get).toHaveBeenCalledTimes(2);
    expect(screen.queryByText('Waiting for approval')).not.toBeInTheDocument();
  });

  it('asks nothing more while nothing waits', async () => {
    get.mockResolvedValue(page([]));
    renderNote();
    await after();
    await after(120_000);
    expect(get).toHaveBeenCalledTimes(1);
  });
});
