import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createFakeFetch } from '@/test/fakeApi';
import type * as RadiologyApi from './api';
import { radiologyApi, radiologyKeys, radiologyQueries, type ImagingStudyDetail, type RadiologyReportDto } from './api';
import { RadiologyReportPanel } from './ReportEditor';

// The saves, the sign-off and the toasts are outside the test; the user is a radiologist who reports and signs.
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof RadiologyApi>()),
  radiologyApi: { saveReport: vi.fn(), submitReport: vi.fn(), verifyReport: vi.fn() },
}));
vi.mock('./AiFindingsPanel', () => ({ AiFindingsPanel: () => null }));
vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

beforeEach(() => {
  vi.clearAllMocks();
  // Fluent's message bars measure themselves; jsdom has no ResizeObserver.
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const study: ImagingStudyDetail = {
  id: 's1',
  accessionNumber: 'CT2610230007',
  patientId: 'p1',
  uhid: 'NH-000123',
  patientName: 'Sunita Kulkarni',
  gender: 'Female',
  age: '54 Y',
  visitId: 'v1',
  admissionId: null,
  patientClass: 'OutPatient',
  sourceOrderNumber: 'ORD-26-0991',
  serviceCode: 'CT-HEAD',
  studyName: 'CT head plain',
  modality: 'CT',
  bodyPart: 'Head',
  priority: 'Routine',
  orderingPractitionerId: 'dr1',
  orderingDoctor: 'Dr. Arjun Rao',
  clinicalNotes: 'Headache for two weeks',
  orderedAt: '2026-10-23T03:00:00Z',
  status: 'Acquired',
  scheduledAt: null,
  room: 'CT 1',
  stationAeTitle: null,
  arrivedAt: '2026-10-23T03:20:00Z',
  startedAt: '2026-10-23T03:25:00Z',
  acquiredAt: '2026-10-23T03:35:00Z',
  technician: 'Ravi Patil',
  contrastUsed: false,
  contrastAgent: null,
  contrastVolumeMl: null,
  doseNote: null,
  technicianNotes: null,
  studyInstanceUid: '1.2.826.0.1.3680043.10.1.7',
  accessionFitsDicom: true,
  worklistProcedureId: 'CT2610230007',
  pacsLinkedAt: null,
  seriesCount: 2,
  instanceCount: 180,
  reportedAt: null,
  verifiedAt: null,
  releasedAt: null,
  cancelReason: null,
  chargeError: null,
  packageBookingId: null,
  quantity: 1,
  patientPreparation: null,
  repeats: [],
  version: 4,
};

/** Version 3 of the draft report, as the form is filled from it. */
const report: RadiologyReportDto = {
  id: 'r1',
  imagingStudyId: 's1',
  templateId: null,
  technique: 'Axial sections without contrast',
  clinicalHistory: 'Headache for two weeks',
  comparison: null,
  findings: 'No intracranial haemorrhage.',
  impression: 'Normal study.',
  isCriticalFinding: false,
  criticalFindingNote: null,
  status: 'Draft',
  reportedBy: null,
  reportedAt: null,
  verifiedBy: null,
  verifiedAt: null,
  releasedAt: null,
  addenda: [],
  version: 3,
};

function renderPanel() {
  // Nothing is read again on its own: the test puts each newer report in the cache as a refetch would.
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  queryClient.setQueryData(radiologyKeys.report(study.id), report);
  queryClient.setQueryData(radiologyQueries.templates(study.modality, study.bodyPart).queryKey, []);
  render(
    <QueryClientProvider client={queryClient}>
      <FluentProvider theme={webLightTheme}>
        <RadiologyReportPanel study={study} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return queryClient;
}

describe('submitting and signing the radiology report (REL-04)', () => {
  it('submits on the version its own save returned', async () => {
    vi.mocked(radiologyApi.saveReport).mockResolvedValue({ ...report, version: 5 });
    vi.mocked(radiologyApi.submitReport).mockResolvedValue({ ...report, status: 'Reported', version: 6 });
    renderPanel();

    await userEvent.click(await screen.findByRole('button', { name: 'Submit for sign-off' }));

    await waitFor(() => expect(radiologyApi.submitReport).toHaveBeenCalledWith('s1', 5));
    expect(radiologyApi.saveReport).toHaveBeenCalledWith('s1', expect.objectContaining({ version: 3 }));
  });

  it('signs an unchanged report on the version the form was filled from, not on one read since', async () => {
    vi.mocked(radiologyApi.verifyReport).mockResolvedValue({ ...report, status: 'Released', version: 5 });
    const queryClient = renderPanel();
    const sign = await screen.findByRole('button', { name: 'Sign and release' });
    // A colleague's change to the findings reached the screen in the background (version 4).
    act(() => {
      queryClient.setQueryData(radiologyKeys.report(study.id), {
        ...report,
        findings: 'Small chronic lacunar infarct in the left basal ganglia.',
        version: 4,
      });
    });

    await userEvent.click(sign);

    // Signed on version 3, which the server refuses: the radiologist has not read the colleague's findings.
    await waitFor(() => expect(radiologyApi.verifyReport).toHaveBeenCalledWith('s1', null, 3, true));
    expect(radiologyApi.saveReport).not.toHaveBeenCalled();
  });

  it('signs a changed report on the version its own save returned', async () => {
    vi.mocked(radiologyApi.saveReport).mockResolvedValue({ ...report, version: 5 });
    vi.mocked(radiologyApi.verifyReport).mockResolvedValue({ ...report, status: 'Released', version: 6 });
    renderPanel();
    await userEvent.type(await screen.findByRole('textbox', { name: 'Comparison' }), 'None available');

    await userEvent.click(screen.getByRole('button', { name: 'Sign and release' }));

    await waitFor(() => expect(radiologyApi.verifyReport).toHaveBeenCalledWith('s1', null, 5, true));
    expect(radiologyApi.saveReport).toHaveBeenCalledWith(
      's1',
      expect.objectContaining({ comparison: 'None available', version: 3 }),
    );
  });

  it('a signature refused as stale reloads the report and says why, as a refused save does', async () => {
    const theirs = { ...report, findings: 'Small chronic lacunar infarct in the left basal ganglia.', version: 4 };
    vi.stubGlobal('fetch', createFakeFetch({ [`GET /api/v1/radiology/studies/${study.id}/report`]: theirs }).fetch);
    vi.mocked(radiologyApi.verifyReport).mockRejectedValue(
      new ApiError({ status: 409, title: 'Changed meanwhile', code: 'concurrency' }),
    );
    renderPanel();

    await userEvent.click(await screen.findByRole('button', { name: 'Sign and release' }));

    await waitFor(() =>
      expect(notify.warning).toHaveBeenCalledWith(
        'The report was changed by someone else',
        'It has been reloaded. Check it and make your changes again.',
      ),
    );
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Findings' })).toHaveValue(theirs.findings));
  });
});
