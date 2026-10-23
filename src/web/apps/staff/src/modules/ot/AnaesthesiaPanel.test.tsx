import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as OtApi from './api';
import { otApi, otKeys, type AnaesthesiaRecordDto, type AnaesthesiaVitals, type SurgeryDetail } from './api';
import { AnaesthesiaPanel } from './AnaesthesiaPanel';

// The saves and the toasts are outside the test; the anaesthetist may write the record.
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof OtApi>()),
  otApi: { saveAnaesthesia: vi.fn(), addVitals: vi.fn() },
}));
vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));
// The vitals chart draws on a canvas jsdom does not have; the table below it lists the same readings.
vi.mock('./shared/TrendChart', () => ({ TrendChart: () => null }));
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

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
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
  anaesthetistId: 'dr2',
  anaesthetistName: 'Dr. Kavita Shah',
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

/** Version 1 of the record, as the form is filled from it. */
const record: AnaesthesiaRecordDto = {
  id: 'r1',
  surgeryBookingId: 'b1',
  patientId: 'p1',
  anaesthetistId: 'dr2',
  asaGrade: 'II',
  asaEmergency: false,
  technique: 'GA',
  airway: 'EndotrachealTube',
  airwayNotes: 'Mallampati 2, 7.5 cuffed tube',
  anaesthesiaStartAt: null,
  anaesthesiaEndAt: null,
  vitals: [],
  drugs: [],
  fluids: [],
  estimatedBloodLossMl: null,
  urineOutputMl: null,
  complications: null,
  recoveryNotes: null,
  signedAt: null,
  signedByName: null,
  version: 1,
};

/** The record as the server keeps it, before anyone saved the form on it: created by the first vitals. */
const blank: AnaesthesiaRecordDto = {
  ...record,
  asaGrade: null,
  airway: 'None',
  airwayNotes: null,
};

const reading = (at: string, heartRate: number): AnaesthesiaVitals => ({
  at,
  heartRate,
  systolic: null,
  diastolic: null,
  spO2: null,
  etCo2: null,
  temperatureC: null,
  respiratoryRate: null,
});

function renderPanel(current: AnaesthesiaRecordDto | null) {
  // Nothing is read again on its own: the test puts each newer record in the cache as a refetch would.
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  queryClient.setQueryData(otKeys.anaesthesia(booking.id), current);
  render(
    <QueryClientProvider client={queryClient}>
      <FluentProvider theme={webLightTheme}>
        <AnaesthesiaPanel booking={booking} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return queryClient;
}

/** Charts a heart rate below the form, which the server answers with the record as `returned`. */
async function addVitals(returned: AnaesthesiaRecordDto) {
  vi.mocked(otApi.addVitals).mockResolvedValueOnce(returned);
  await userEvent.type(await screen.findByRole('spinbutton', { name: 'Heart rate' }), '72');
  await userEvent.click(screen.getByRole('button', { name: 'Add reading' }));
  await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Vitals added'));
}

/** Saves the form as a draft and returns the version it went with. */
async function saveDraft() {
  vi.mocked(otApi.saveAnaesthesia).mockResolvedValue({ ...record, version: 99 });
  await userEvent.type(screen.getByRole('spinbutton', { name: 'Urine output (ml)' }), '150');
  await userEvent.click(screen.getByRole('button', { name: 'Save draft' }));
  await waitFor(() => expect(otApi.saveAnaesthesia).toHaveBeenCalledTimes(1));
  return vi.mocked(otApi.saveAnaesthesia).mock.calls[0]![1].version;
}

describe('the anaesthesia record form and the vitals charted below it (REL-04)', () => {
  it("keeps the version it was filled from when a colleague changed the form's fields before the vitals", async () => {
    renderPanel(record);

    // A colleague saved a complication (version 2), which this screen has not read; the vitals come back on top of it.
    await addVitals({
      ...record,
      complications: 'Bronchospasm at induction, settled with salbutamol',
      vitals: [reading('2026-10-23T03:50:00Z', 72)],
      version: 3,
    });

    // The save goes on version 1, which the server refuses: it does not write over the colleague's complication.
    expect(await saveDraft()).toBe(1);
  });

  it('follows the vitals when they changed only the vitals: the save goes on the version they returned', async () => {
    renderPanel(record);

    await addVitals({ ...record, vitals: [reading('2026-10-23T03:50:00Z', 72)], version: 2 });

    expect(await saveDraft()).toBe(2);
  });

  it("follows the vitals over a colleague's vitals already shown, which are not the form's to save", async () => {
    const queryClient = renderPanel(record);
    // A colleague charted a reading (version 2), and the screen read it.
    const theirs = { ...record, vitals: [reading('2026-10-23T03:45:00Z', 70)], version: 2 };
    act(() => {
      queryClient.setQueryData(otKeys.anaesthesia(booking.id), theirs);
    });

    await addVitals({ ...theirs, vitals: [...theirs.vitals, reading('2026-10-23T03:50:00Z', 72)], version: 3 });

    expect(await saveDraft()).toBe(3);
  });

  it('follows the first vitals, which create the record, while nobody saved the form on it', async () => {
    renderPanel(null);

    await addVitals({ ...blank, vitals: [reading('2026-10-23T03:50:00Z', 72)], version: 1 });

    expect(await saveDraft()).toBe(1);
  });

  it('stays without a version when a colleague saved the form before the first vitals', async () => {
    renderPanel(null);

    // A colleague saved the form first (creating the record); the vitals come back on top of what they saved.
    await addVitals({ ...record, vitals: [reading('2026-10-23T03:50:00Z', 72)], version: 2 });

    // Saved without a version over a record that exists: refused, not written over the colleague's form.
    expect(await saveDraft()).toBeNull();
  });
});
