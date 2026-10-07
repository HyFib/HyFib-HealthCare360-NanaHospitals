import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { deferred, dropFocusIfDisabled } from '@/test/busyButtons';
import { privacyKeys, type DataRequestDetail, type DataSummary } from './api';
import { DataSummaryPanel } from './DataSummaryPanel';

// The summary comes from the query cache, and the printing and the toasts are outside the test: what is tested is
// where focus stays while the summary prints.
const printDataSummary = vi.hoisted(() => vi.fn());
vi.mock('./print', () => ({ printDataSummary }));
const notify = vi.hoisted(() => ({ success: vi.fn(), warning: vi.fn(), error: vi.fn(), info: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const request = {
  id: 'r1',
  requestNumber: 'NHDR26000001',
  type: 'Access',
  status: 'InProgress',
  patientId: 'p1',
  hasSummary: false,
  closedAt: null,
} as DataRequestDetail;

const summary: DataSummary = {
  generatedAt: '2026-10-07T04:30:00Z',
  hospital: 'Nana Hospitals',
  person: {
    uhid: 'NH26000001',
    fullName: 'Meena Kumari',
    gender: 'Female',
    dateOfBirth: null,
    phone: null,
    alternatePhone: null,
    email: null,
    address: null,
    emergencyContact: null,
    abhaNumber: null,
    abhaAddress: null,
    aadhaarLast4: null,
    hasPhoto: false,
    registeredAt: '2026-01-05T04:30:00Z',
    registeredAtFacility: null,
  },
  categories: [],
  consents: [],
  sharedWith: [],
  processors: [],
  nominee: null,
  contact: { title: 'Data Protection Officer', name: null, email: null, phone: null, address: null },
  periods: { outpatientYears: 3, inpatientYears: 10, financeYears: 8, messageLogDays: 180, auditYears: 7 },
};

function renderPanel() {
  const queryClient = new QueryClient();
  queryClient.setQueryData(privacyKeys.summary(request.id), summary);
  render(
    <QueryClientProvider client={queryClient}>
      <FluentProvider theme={webLightTheme}>
        <DataSummaryPanel request={request} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('DataSummaryPanel', () => {
  beforeEach(() => {
    printDataSummary.mockReset();
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('keeps focus on Print summary while the summary prints, and prints it once', async () => {
    const printing = deferred();
    printDataSummary.mockReturnValue(printing.promise);
    renderPanel();
    const print = await screen.findByRole('button', { name: 'Print summary' });

    print.focus();
    await userEvent.keyboard('{Enter}');
    expect(printDataSummary).toHaveBeenCalledWith(summary, request.requestNumber);
    dropFocusIfDisabled(print);

    expect(print).toHaveFocus();
    expect(print).toHaveAttribute('aria-disabled', 'true');
    await userEvent.keyboard('{Enter}');
    expect(printDataSummary).toHaveBeenCalledTimes(1);

    printing.resolve();
    await waitFor(() => expect(print).not.toHaveAttribute('aria-disabled'));
    expect(print).toHaveFocus();
  });
});
