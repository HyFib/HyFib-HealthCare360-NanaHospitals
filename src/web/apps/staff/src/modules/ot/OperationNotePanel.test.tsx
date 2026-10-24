import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import type * as Letterhead from '@/lib/letterhead';
import type * as OtApi from './api';
import { otApi, otKeys, type OperationNoteDto, type SurgeryDetail } from './api';
import { OperationNotePanel } from './OperationNotePanel';

// The save and the toasts are outside the test; the surgeon may write the note.
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof OtApi>()),
  otApi: { saveOperationNote: vi.fn() },
}));
vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));
vi.mock('@/lib/letterhead', async (importOriginal) => ({
  ...(await importOriginal<typeof Letterhead>()),
  useLetterhead: () => ({ name: 'Nana Hospitals' }),
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

// Fluent's message bars measure themselves; jsdom has no ResizeObserver.
beforeAll(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

const booking: SurgeryDetail = {
  id: 'b1',
  bookingNumber: 'OT-26-0042',
  patient: null,
  patientId: 'p1',
  admissionId: 'a1',
  visitId: null,
  orderItemId: null,
  procedureServiceItemId: null,
  procedureCode: null,
  procedureName: 'Laparoscopic cholecystectomy',
  laterality: 'NotApplicable',
  diagnosis: 'Symptomatic cholelithiasis',
  surgeonId: 'dr1',
  surgeonName: 'Dr. Arjun Rao',
  assistantIds: [],
  anaesthetistId: null,
  anaesthetistName: null,
  anaesthesiaType: 'GA',
  priority: 'Elective',
  preferredDate: null,
  theatreId: 't1',
  theatreName: 'OT 1',
  scheduledStart: '2026-10-23T03:30:00Z',
  scheduledEnd: '2026-10-23T05:00:00Z',
  status: 'InProgress',
  receivedInPreOpAt: '2026-10-23T03:00:00Z',
  inRoomAt: '2026-10-23T03:25:00Z',
  startedAt: '2026-10-23T03:40:00Z',
  completedAt: null,
  preOpComplete: true,
  preOpChecklist: [],
  safetyChecklist: [],
  notes: null,
  cancelReason: null,
  postponeReason: null,
  postponeCount: 0,
  chargeStatus: 'NotDue',
  chargeError: null,
  version: 11,
};

const note: OperationNoteDto = {
  id: 'n1',
  surgeryBookingId: 'b1',
  patientId: 'p1',
  surgeonId: 'dr1',
  preOperativeDiagnosis: 'Symptomatic cholelithiasis',
  postOperativeDiagnosis: null,
  procedurePerformed: 'Laparoscopic cholecystectomy',
  findings: 'Distended gall bladder',
  procedureDetails: null,
  specimens: [],
  drains: null,
  estimatedBloodLossMl: null,
  complications: null,
  postOpOrders: null,
  signedAt: null,
  signedByName: null,
  version: 5,
};

function renderPanel() {
  // Nothing is read again on its own: the test puts each newer note in the cache as a refetch would.
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  queryClient.setQueryData(otKeys.operationNote(booking.id), note);
  render(
    <QueryClientProvider client={queryClient}>
      <FluentProvider theme={webLightTheme}>
        <OperationNotePanel booking={booking} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return queryClient;
}

describe('the operation note', () => {
  it('saves on the version it was filled from, then on the one its own save returned (REL-04)', async () => {
    vi.mocked(otApi.saveOperationNote).mockImplementation(async (_, request) => ({
      ...note,
      findings: request.findings,
      version: request.version === 5 ? 7 : 9,
    }));
    const queryClient = renderPanel();

    const drains = await screen.findByRole('textbox', { name: 'Drains' });
    await userEvent.type(drains, 'Subhepatic drain');
    // Another surgeon's save reached this screen in the background (version 6), while the form shows version 5.
    act(() => {
      queryClient.setQueryData(otKeys.operationNote(booking.id), {
        ...note,
        findings: 'Distended gall bladder, adhesions',
        version: 6,
      });
    });
    await userEvent.click(screen.getByRole('button', { name: 'Save draft' }));

    await waitFor(() => expect(otApi.saveOperationNote).toHaveBeenCalledTimes(1));
    expect(otApi.saveOperationNote).toHaveBeenLastCalledWith(
      'b1',
      expect.objectContaining({ findings: 'Distended gall bladder', drains: 'Subhepatic drain', version: 5 }),
    );

    // Had it been accepted, the next save goes on the version that save returned, not on one read since.
    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Operation note saved'));
    act(() => {
      queryClient.setQueryData(otKeys.operationNote(booking.id), { ...note, version: 8 });
    });
    await userEvent.type(drains, ', removed on day 2');
    await userEvent.click(screen.getByRole('button', { name: 'Save draft' }));

    await waitFor(() => expect(otApi.saveOperationNote).toHaveBeenCalledTimes(2));
    expect(otApi.saveOperationNote).toHaveBeenLastCalledWith(
      'b1',
      expect.objectContaining({ drains: 'Subhepatic drain, removed on day 2', version: 7 }),
    );
  });
});
