import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError, type PagedResult } from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type * as Router from '@tanstack/react-router';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import type { CardiologyStudyDetail } from '@/modules/cardiology/api';
import type { ReportStudyRef } from '@/modules/lab/common/types';
import type { ImagingStudyDetail, RadiologyReportDto, StudyImages, ViewerLink } from '@/modules/radiology/api';
import type { LabReportView } from './api';
import type { TimelineEntry } from './timeline';
import { TimelinePanel } from './TimelinePanel';

// The chart's timeline with the server, the signed-in user's permissions and the router replaced: a released result
// opens its report in the chart (IMG-03), with the images and the PACS viewer of an imaging study in PACS (IMG-02).

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), blob: vi.fn() } }));
const granted = vi.hoisted(() => ({ permissions: [] as string[] }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (permission: string | readonly string[] | undefined) =>
    permission === undefined ||
    (typeof permission === 'string' ? [permission] : permission).some((p) => granted.permissions.includes(p)),
}));
const navigate = vi.hoisted(() => vi.fn());
vi.mock('@tanstack/react-router', async (importOriginal) => ({
  ...(await importOriginal<typeof Router>()),
  useNavigate: () => navigate,
}));

const CHART = ['clinical.emr.read'];
const DOCTOR = [
  ...CHART,
  'lab.orders.read',
  'radiology.studies.read',
  'radiology.images.view',
  'cardiology.studies.read',
];
/** Nurses read the reports but not the images (radiology.images.view). */
const NURSE = [...CHART, 'lab.orders.read', 'radiology.studies.read', 'cardiology.studies.read'];

const PATIENT = '0192c0de-0000-7000-8000-000000000001';
const XRAY_REPORT = '0192c0de-0000-7000-8000-00000000a001';
const XRAY_STUDY = '0192c0de-0000-7000-8000-00000000b001';
const LAB_REPORT = '0192c0de-0000-7000-8000-00000000a002';
const ECG_REPORT = '0192c0de-0000-7000-8000-00000000a003';
const ECG_STUDY = '0192c0de-0000-7000-8000-00000000b003';

const result = (patch: Partial<TimelineEntry>): TimelineEntry => ({
  type: 'Result',
  id: XRAY_REPORT,
  occurredAt: '2026-10-02T06:00:00Z',
  title: 'X-ray chest PA',
  summary: 'Radiology',
  status: 'Reviewed',
  encounterId: null,
  isCritical: false,
  reportKind: 'Radiology',
  ...patch,
});

const TIMELINE: PagedResult<TimelineEntry> = {
  items: [
    result({}),
    result({
      id: LAB_REPORT,
      occurredAt: '2026-10-02T05:30:00Z',
      title: 'Complete blood count',
      summary: 'Laboratory',
      reportKind: 'Laboratory',
    }),
    result({
      id: ECG_REPORT,
      occurredAt: '2026-10-02T05:00:00Z',
      title: 'ECG',
      summary: 'Cardiology',
      reportKind: 'Cardiology',
    }),
  ],
  page: 1,
  pageSize: 30,
  totalCount: 3,
  totalPages: 1,
};

const XRAY: ImagingStudyDetail = {
  id: XRAY_STUDY,
  accessionNumber: 'NHRD260000042',
  patientId: PATIENT,
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  gender: 'Female',
  age: '39Y',
  visitId: null,
  admissionId: null,
  patientClass: 'OutPatient',
  sourceOrderNumber: 'NHOR260000101',
  serviceCode: 'RAD-XR-CHEST',
  studyName: 'X-ray chest PA',
  modality: 'XR',
  bodyPart: 'Chest',
  priority: 'Routine',
  orderingPractitionerId: null,
  orderingDoctor: 'Dr. Meera Iyer',
  clinicalNotes: 'Cough for two weeks',
  orderedAt: '2026-10-02T03:00:00Z',
  status: 'Released',
  scheduledAt: null,
  room: null,
  stationAeTitle: null,
  arrivedAt: '2026-10-02T03:30:00Z',
  startedAt: '2026-10-02T03:35:00Z',
  acquiredAt: '2026-10-02T03:40:00Z',
  technician: 'Ravi K',
  contrastUsed: false,
  contrastAgent: null,
  contrastVolumeMl: null,
  doseNote: null,
  technicianNotes: null,
  studyInstanceUid: '2.25.1',
  accessionFitsDicom: true,
  worklistProcedureId: 'NHRD260000042',
  pacsLinkedAt: '2026-10-02T03:45:00Z',
  seriesCount: 1,
  instanceCount: 1,
  reportedAt: '2026-10-02T05:50:00Z',
  verifiedAt: '2026-10-02T06:00:00Z',
  releasedAt: '2026-10-02T06:00:00Z',
  cancelReason: null,
  chargeError: null,
  packageBookingId: null,
  quantity: 1,
  patientPreparation: null,
  repeats: [],
  version: 7,
};

const XRAY_REPORT_DTO: RadiologyReportDto = {
  id: XRAY_REPORT,
  imagingStudyId: XRAY_STUDY,
  templateId: null,
  technique: 'PA view',
  clinicalHistory: 'Cough for two weeks',
  comparison: null,
  findings: 'Both lung fields are clear. Heart size is normal.',
  impression: 'No active lung lesion.',
  isCriticalFinding: false,
  criticalFindingNote: null,
  status: 'Released',
  reportedBy: 'Dr. Kiran Rao',
  reportedAt: '2026-10-02T05:50:00Z',
  verifiedBy: 'Dr. Kiran Rao',
  verifiedAt: '2026-10-02T06:00:00Z',
  releasedAt: '2026-10-02T06:00:00Z',
  addenda: [],
  version: 3,
};

const XRAY_IMAGES: StudyImages = {
  studyId: XRAY_STUDY,
  accessionNumber: 'NHRD260000042',
  studyInstanceUid: '2.25.1',
  series: [
    {
      seriesInstanceUid: '2.25.1.1',
      seriesNumber: 1,
      modality: 'CR',
      description: 'Chest PA',
      bodyPart: null,
      imageCount: 1,
    },
  ],
};

const VIEWER: ViewerLink = {
  studyId: XRAY_STUDY,
  url: 'https://pacs.example.org/viewer?acc=NHRD260000042',
  studyInstanceUid: '2.25.1',
};

const LAB: LabReportView = {
  reportId: LAB_REPORT,
  reportNumber: 'NHLB260000077',
  status: 'Released',
  isPartial: false,
  releasedAt: '2026-10-02T05:30:00Z',
  sections: [
    {
      section: 'Haematology',
      tests: [
        {
          orderItemId: 'i1',
          name: 'Complete blood count',
          interpretation: null,
          results: [
            {
              resultId: 'r1',
              name: 'Haemoglobin',
              groupName: null,
              value: '9.1',
              unit: 'g/dL',
              flag: 'Low',
              referenceText: '12.0 - 15.0',
              remarks: null,
            },
          ],
        },
      ],
    },
  ],
};

const ECG: CardiologyStudyDetail = {
  id: ECG_STUDY,
  accessionNumber: 'NHCD260000011',
  patientId: PATIENT,
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  gender: 'Female',
  age: '39Y',
  visitId: null,
  admissionId: null,
  patientClass: 'OutPatient',
  sourceOrderNumber: null,
  serviceCode: 'CARD-ECG',
  studyName: 'ECG',
  studyType: 'Ecg',
  priority: 'Routine',
  orderingPractitionerId: null,
  orderingDoctor: null,
  clinicalNotes: null,
  orderedAt: '2026-10-02T04:00:00Z',
  status: 'Released',
  scheduledAt: null,
  room: null,
  startedAt: '2026-10-02T04:10:00Z',
  acquiredAt: '2026-10-02T04:15:00Z',
  technician: null,
  deviceName: null,
  technicianNotes: null,
  reportedAt: '2026-10-02T04:50:00Z',
  verifiedAt: '2026-10-02T05:00:00Z',
  releasedAt: '2026-10-02T05:00:00Z',
  cancelReason: null,
  chargeError: null,
  packageBookingId: null,
  quantity: 1,
  files: [
    {
      id: 'f1',
      fileName: 'ecg.dcm',
      contentType: 'application/dicom',
      sizeBytes: 40960,
      pacs: { status: 'Stored', message: null, sentAt: '2026-10-02T04:20:00Z' },
    },
  ],
  report: {
    id: ECG_REPORT,
    studyType: 'Ecg',
    ecg: {
      heartRate: 76,
      rhythm: 'Sinus rhythm',
      prInterval: null,
      qrsDuration: null,
      qtInterval: null,
      qtcInterval: null,
      axis: null,
      stTChanges: null,
      interpretation: null,
    },
    echo: {
      lvef: null,
      leftVentricle: null,
      rightVentricle: null,
      leftAtrium: null,
      rightAtrium: null,
      valves: null,
      rwma: null,
      diastolicFunction: null,
      pericardium: null,
      conclusion: null,
    },
    tmt: {
      protocol: null,
      durationSeconds: null,
      mets: null,
      maxHeartRate: null,
      targetHeartRatePercent: null,
      bpResponse: null,
      symptoms: null,
      stChanges: null,
      reasonForTermination: null,
      result: null,
    },
    holter: {
      durationHours: null,
      minHeartRate: null,
      meanHeartRate: null,
      maxHeartRate: null,
      ventricularEctopics: null,
      supraventricularEctopics: null,
      pauses: null,
      longestPause: null,
      arrhythmias: null,
      stChanges: null,
    },
    abpm: {
      daytimeMeanSystolic: null,
      daytimeMeanDiastolic: null,
      nighttimeMeanSystolic: null,
      nighttimeMeanDiastolic: null,
      dippingPattern: null,
    },
    conclusion: 'Normal ECG.',
    recommendations: null,
    isCriticalFinding: false,
    criticalFindingNote: null,
    status: 'Released',
    reportedBy: 'Dr. Arjun Shah',
    reportedAt: '2026-10-02T04:50:00Z',
    verifiedBy: 'Dr. Arjun Shah',
    verifiedAt: '2026-10-02T05:00:00Z',
    releasedAt: '2026-10-02T05:00:00Z',
    version: 2,
  },
  version: 5,
};

const studyRef = (studyId: string, reportId: string): ReportStudyRef => ({ studyId, reportId, status: 'Released' });

/** The server: each path's answer (an ApiError is refused with it); anything else is not found. */
function answer(overrides: Record<string, unknown> = {}) {
  const paths: Record<string, unknown> = {
    [`/api/v1/emr/patients/${PATIENT}/timeline`]: TIMELINE,
    '/api/v1/masters/lookups/departments': [],
    [`/api/v1/radiology/reports/${XRAY_REPORT}`]: studyRef(XRAY_STUDY, XRAY_REPORT),
    [`/api/v1/radiology/studies/${XRAY_STUDY}`]: XRAY,
    [`/api/v1/radiology/studies/${XRAY_STUDY}/report`]: XRAY_REPORT_DTO,
    [`/api/v1/radiology/studies/${XRAY_STUDY}/images`]: XRAY_IMAGES,
    [`/api/v1/radiology/studies/${XRAY_STUDY}/viewer`]: VIEWER,
    [`/api/v1/lab/reports/${LAB_REPORT}`]: LAB,
    [`/api/v1/cardiology/reports/${ECG_REPORT}`]: studyRef(ECG_STUDY, ECG_REPORT),
    [`/api/v1/cardiology/studies/${ECG_STUDY}`]: ECG,
    ...overrides,
  };
  vi.mocked(api.get).mockImplementation((path: string) => {
    const found = paths[path];
    return found === undefined
      ? Promise.reject(new ApiError({ status: 404, title: 'Not found' }))
      : found instanceof Error
        ? Promise.reject(found)
        : Promise.resolve(found);
  });
  vi.mocked(api.blob).mockResolvedValue(new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' }));
}

/** The paths the chart asked the server for. */
const asked = () => vi.mocked(api.get).mock.calls.map((call) => call[0]);

function renderTimeline() {
  const onOpen = vi.fn();
  render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <TimelinePanel patientId={PATIENT} filters={{}} onFiltersChange={vi.fn()} onOpen={onOpen} />
      </QueryClientProvider>
    </FluentProvider>,
  );
  return { onOpen };
}

beforeAll(() => {
  // jsdom has neither object URLs nor ResizeObserver (Fluent's dialogs and message bars measure themselves).
  URL.createObjectURL = vi.fn(() => 'blob:image');
  URL.revokeObjectURL = vi.fn();
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
  granted.permissions = DOCTOR;
});

afterEach(() => {
  vi.mocked(api.get).mockReset();
  vi.mocked(api.blob).mockReset();
  navigate.mockReset();
});

describe('a result on the chart', () => {
  it('opens its imaging report, with the images and the PACS viewer of a study linked with PACS', async () => {
    answer();
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);
    const { onOpen } = renderTimeline();

    await userEvent.click(await screen.findByText('X-ray chest PA'));

    const dialog = await screen.findByRole('dialog', { name: 'X-ray chest PA' });
    expect(await within(dialog).findByText('No active lung lesion.')).toBeInTheDocument();
    expect(within(dialog).getByText('Both lung fields are clear. Heart size is normal.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Print' })).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Download PDF' })).toBeInTheDocument();
    expect(
      await within(dialog).findByRole('button', { name: 'Open Series 1 · Chest PA · 1 image' }),
    ).toBeInTheDocument();
    expect(onOpen).not.toHaveBeenCalled();
    await expectNoAxeViolations();

    // The PACS viewer opens through the radiology endpoint, as on the study screen.
    await userEvent.click(within(dialog).getByRole('button', { name: 'Open in PACS viewer' }));
    await waitFor(() => expect(open).toHaveBeenCalledWith(VIEWER.url, '_blank', 'noopener,noreferrer'));
    expect(asked()).toContain(`/api/v1/radiology/studies/${XRAY_STUDY}/viewer`);

    await userEvent.click(within(dialog).getByRole('button', { name: 'Open study' }));
    expect(navigate).toHaveBeenCalledWith({ to: '/radiology/studies/$studyId', params: { studyId: XRAY_STUDY } });
    open.mockRestore();
  });

  it('offers no images or viewer for a study not linked with PACS', async () => {
    answer({ [`/api/v1/radiology/studies/${XRAY_STUDY}`]: { ...XRAY, pacsLinkedAt: null } });
    renderTimeline();

    await userEvent.click(await screen.findByText('X-ray chest PA'));

    const dialog = await screen.findByRole('dialog', { name: 'X-ray chest PA' });
    expect(await within(dialog).findByText('No active lung lesion.')).toBeInTheDocument();
    expect(within(dialog).getByText('The images are not in HC360 yet.')).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Open in PACS viewer' })).not.toBeInTheDocument();
    expect(asked()).not.toContain(`/api/v1/radiology/studies/${XRAY_STUDY}/images`);
  });

  it('offers the images and the viewer only to staff who may see images', async () => {
    granted.permissions = NURSE;
    answer();
    renderTimeline();

    await userEvent.click(await screen.findByText('X-ray chest PA'));

    const dialog = await screen.findByRole('dialog', { name: 'X-ray chest PA' });
    expect(await within(dialog).findByText('No active lung lesion.')).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Open in PACS viewer' })).not.toBeInTheDocument();
    expect(within(dialog).queryByText('The images are not in HC360 yet.')).not.toBeInTheDocument();
    expect(asked()).not.toContain(`/api/v1/radiology/studies/${XRAY_STUDY}/images`);
  });

  it('opens a lab report, to print or download from the server', async () => {
    answer();
    renderTimeline();

    await userEvent.click(await screen.findByText('Complete blood count'));

    const dialog = await screen.findByRole('dialog', { name: 'Complete blood count' });
    expect(await within(dialog).findByText('Haemoglobin')).toBeInTheDocument();
    expect(within(dialog).getByText('9.1 g/dL (Low)')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Print' })).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Download PDF' })).toBeInTheDocument();
    await expectNoAxeViolations();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Full report' }));
    expect(navigate).toHaveBeenCalledWith({ href: `/lab/reports/${LAB_REPORT}` });
  });

  it('opens a cardiology report, with the PACS viewer once the PACS holds its recordings', async () => {
    answer();
    renderTimeline();

    await userEvent.click(await screen.findByText('ECG'));

    const dialog = await screen.findByRole('dialog', { name: 'ECG' });
    expect(await within(dialog).findByText('Normal ECG.')).toBeInTheDocument();
    expect(within(dialog).getByText('Sinus rhythm')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Open in PACS viewer' })).toBeInTheDocument();
  });

  it('says why when the record is restricted', async () => {
    answer({
      [`/api/v1/radiology/studies/${XRAY_STUDY}`]: new ApiError({
        status: 403,
        title: 'Not allowed',
        code: 'patient.restricted',
        detail: 'This is a restricted record.',
      }),
    });
    renderTimeline();

    await userEvent.click(await screen.findByText('X-ray chest PA'));

    const dialog = await screen.findByRole('dialog', { name: 'X-ray chest PA' });
    expect(
      await within(dialog).findByText(
        "This patient's record is restricted. Open the chart and ask for access to read the report.",
      ),
    ).toBeInTheDocument();
    expect(asked()).not.toContain(`/api/v1/radiology/studies/${XRAY_STUDY}/report`);
    expect(within(dialog).queryByRole('button', { name: 'Open in PACS viewer' })).not.toBeInTheDocument();
  });

  it('says why a report the department does not show here cannot be opened', async () => {
    // The record lists every facility's results; the departments show their own facility's.
    answer({ [`/api/v1/radiology/reports/${XRAY_REPORT}`]: undefined });
    renderTimeline();

    await userEvent.click(await screen.findByText('X-ray chest PA'));

    const dialog = await screen.findByRole('dialog', { name: 'X-ray chest PA' });
    expect(
      await within(dialog).findByText(
        'This report cannot be opened here. It may be from another facility, or have been withdrawn; check with the radiology department.',
      ),
    ).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Open study' })).not.toBeInTheDocument();
  });

  it('leads to the orders, as before, for staff who may not open the report', async () => {
    granted.permissions = CHART;
    answer();
    const { onOpen } = renderTimeline();

    await userEvent.click(await screen.findByText('Complete blood count'));

    expect(onOpen).toHaveBeenCalledWith({ kind: 'tab', tab: 'orders' });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(asked()).not.toContain(`/api/v1/lab/reports/${LAB_REPORT}`);
  });
});
