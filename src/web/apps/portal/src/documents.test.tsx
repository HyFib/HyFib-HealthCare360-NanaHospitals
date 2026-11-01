import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { useSession, type AuthUser } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { DocumentActions } from '@/components/DocumentActions';
import { useLanguageStore } from '@/i18n';
import { useProfileStore } from '@/lib/profileStore';
import type {
  CardiologyReportView,
  DischargeSummaryDto,
  LabReportView,
  PortalBillDetail,
  PortalPrescriptionDetail,
  PortalProfile,
  RadiologyReportView,
  ReportLetterhead,
  ReportPatient,
} from '@/lib/types';
import { createPortalRouter } from '@/router';
import { catchDownloads, type SavedFile } from '@/test/downloads';

/**
 * "Download PDF" on the documents a patient keeps (reports, prescriptions, discharge summaries, bills), end to end in
 * jsdom (router, signed-in layout, queries, i18n) against a fake server, with a patient already signed in.
 */

const PATIENT_ID = '5f0c6d3e-8c1a-4a53-9a3f-2b1d6f7e9a10';
const LAB_ID = '1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d';
const SCAN_ID = '2b3c4d5e-6f7a-4b8c-9d0e-1f2a3b4c5d6e';
const HEART_ID = '3c4d5e6f-7a8b-4c9d-8e1f-2a3b4c5d6e7f';
const RX_ID = '4d5e6f7a-8b9c-4d0e-9f2a-3b4c5d6e7f8a';
const RX_AWAITING_DSC_ID = '7a8b9c0d-1e2f-4a3b-9c4d-5e6f7a8b9c0d';
const SUMMARY_ID = '5e6f7a8b-9c0d-4e1f-8a3b-4c5d6e7f8a9b';
const BILL_ID = '6f7a8b9c-0d1e-4f2a-9b4c-5d6e7f8a9b0c';
const BILL_PDF = `/api/v1/portal/bills/${BILL_ID}/pdf`;
const RELEASED = '2026-09-28T08:00:00Z';
const SLOW = { timeout: 8000 };
const HINT =
  "Download PDF gives you the hospital's official copy. Print gives you a copy of what you see on this page.";

const PROFILE: PortalProfile = {
  id: PATIENT_ID,
  uhid: 'NH26000123',
  fullName: 'Asha Verma',
  gender: 'Female',
  dateOfBirth: '1987-03-14',
  isDateOfBirthEstimated: false,
  age: '39Y',
  phone: '+91******3210',
  bloodGroup: 'BPositive',
  category: 'General',
  isVip: false,
  isDeceased: false,
  photoFileId: null,
};

const USER: AuthUser = {
  id: 'user-1',
  userName: '+919876543210',
  displayName: 'Asha Verma',
  type: 'patient',
  facilityId: null,
  facilityName: null,
  practitionerId: null,
  patientId: PATIENT_ID,
  roles: [],
  permissions: ['portal.access'],
  facilities: [],
  mfaEnabled: false,
  hasPin: false,
  mustChangePassword: false,
  restriction: null,
};

const LETTERHEAD: ReportLetterhead = {
  facilityName: 'Nana Hospitals, Pune',
  address: null,
  phone: null,
  email: null,
  nabhNumber: null,
  letterheadNote: null,
  logoFileId: null,
};

const PATIENT: ReportPatient = {
  id: PATIENT_ID,
  uhid: 'NH26000123',
  name: 'Asha Verma',
  gender: 'Female',
  age: '39Y',
  dateOfBirth: '1987-03-14',
};

const LAB: LabReportView = {
  reportId: LAB_ID,
  labOrderId: '7a8b9c0d-1e2f-4a3b-8c5d-6e7f8a9b0c1d',
  reportNumber: 'LAB2600123',
  status: 'Released',
  isPartial: false,
  revision: 1,
  letterhead: LETTERHEAD,
  patient: PATIENT,
  patientClass: 'OP',
  referredBy: null,
  clinicalNotes: null,
  orderedAt: '2026-09-28T04:00:00Z',
  firstReleasedAt: RELEASED,
  releasedAt: RELEASED,
  sections: [
    {
      section: 'Hematology',
      tests: [
        {
          orderItemId: '8b9c0d1e-2f3a-4b4c-9d6e-7f8a9b0c1d2e',
          code: 'HB',
          name: 'Haemoglobin',
          status: 'Released',
          specimenType: 'Blood',
          method: null,
          interpretation: null,
          referralLabName: null,
          collectedAt: null,
          receivedAt: null,
          verifiedAt: null,
          releasedAt: RELEASED,
          results: [
            {
              resultId: '9c0d1e2f-3a4b-4c5d-8e7f-8a9b0c1d2e3f',
              name: 'Haemoglobin',
              groupName: null,
              value: '13.2',
              unit: 'g/dL',
              flag: 'Normal',
              referenceText: '12.0 - 15.0',
              status: 'Released',
              autoValidated: false,
              remarks: null,
              organism: null,
              colonyCount: null,
              susceptibilities: [],
              previousValue: null,
              previousResultAt: null,
            },
          ],
        },
      ],
    },
  ],
  signatories: [],
  hasAutoValidatedResults: false,
  amendments: [],
  verificationCode: null,
  verificationPath: null,
  verificationUrl: null,
};

const SCAN: RadiologyReportView = {
  reportId: '0d1e2f3a-4b5c-4d6e-9f8a-9b0c1d2e3f4a',
  imagingStudyId: SCAN_ID,
  letterhead: LETTERHEAD,
  patient: PATIENT,
  accessionNumber: 'RAD2600045',
  studyName: 'Chest X-ray',
  modality: 'XR',
  bodyPart: 'Chest',
  referredBy: null,
  orderedAt: '2026-09-28T04:00:00Z',
  acquiredAt: null,
  technique: null,
  clinicalHistory: null,
  comparison: null,
  findings: 'The lungs are clear.',
  impression: 'Normal chest X-ray.',
  isCriticalFinding: false,
  status: 'Released',
  verifiedAt: null,
  releasedAt: RELEASED,
  signatories: [],
  addenda: [],
  verificationCode: null,
  verificationPath: null,
  verificationUrl: null,
};

const HEART: CardiologyReportView = {
  reportId: '1e2f3a4b-5c6d-4e7f-8a9b-0c1d2e3f4a5b',
  cardiologyStudyId: HEART_ID,
  letterhead: LETTERHEAD,
  patient: PATIENT,
  accessionNumber: 'CAR2600007',
  studyName: 'Resting ECG',
  studyType: 'Ecg',
  referredBy: null,
  orderedAt: '2026-09-28T04:00:00Z',
  acquiredAt: null,
  ecg: null,
  echo: null,
  tmt: null,
  holter: null,
  abpm: null,
  conclusion: 'Normal ECG.',
  recommendations: null,
  isCriticalFinding: false,
  status: 'Released',
  verifiedAt: null,
  releasedAt: RELEASED,
  signatories: [],
  verificationCode: null,
  verificationPath: null,
  verificationUrl: null,
};

const RX: PortalPrescriptionDetail = {
  id: RX_ID,
  prescriptionNumber: 'RX2600123',
  patientId: PATIENT_ID,
  status: 'Signed',
  signedAt: '2026-09-28T06:00:00Z',
  doctor: 'Dr. Meera Iyer',
  doctorQualification: 'MBBS, MD',
  doctorRegistrationNumber: 'KMC 12345',
  facility: 'Nana Hospitals, Pune',
  advice: null,
  lines: [
    {
      drugName: 'Paracetamol',
      genericName: null,
      strength: '500 mg',
      dosageForm: 'Tablet',
      dose: '1 tablet',
      frequency: 'TDS',
      frequencyEnglish: 'Three times a day',
      frequencyHindi: 'दिन में तीन बार',
      frequencyTamil: 'நாளொன்றுக்கு மூன்று முறை',
      durationDays: 3,
      quantity: 9,
      instructions: null,
    },
  ],
  refillsRemaining: 0,
  verificationToken: null,
  verificationUrl: null,
};

/** Signed in HC360, at a hospital that issues prescriptions only once the doctor has signed them with their DSC. */
const RX_AWAITING_DSC: PortalPrescriptionDetail = {
  ...RX,
  id: RX_AWAITING_DSC_ID,
  prescriptionNumber: 'RX2600124',
  finalCopyAvailable: false,
  digitalSignatureState: 'NotSigned',
  verificationToken: null,
  verificationUrl: null,
};

const SUMMARY: DischargeSummaryDto = {
  id: SUMMARY_ID,
  admissionId: '2f3a4b5c-6d7e-4f8a-9b0c-1d2e3f4a5b6c',
  patientId: PATIENT_ID,
  encounterId: null,
  practitionerId: null,
  practitionerName: 'Dr. Meera Iyer',
  admittedAt: '2026-09-20T06:00:00Z',
  dischargedAt: '2026-09-25T06:00:00Z',
  diagnoses: 'Dengue fever',
  diagnosisCodes: ['A90'],
  presentingComplaints: null,
  courseInHospital: null,
  procedures: null,
  investigationsSummary: null,
  conditionAtDischarge: null,
  medications: null,
  advice: 'Drink plenty of fluids.',
  followUpDate: null,
  followUpInstructions: null,
  status: 'Signed',
  signedAt: '2026-09-25T07:00:00Z',
  signedByPractitionerId: null,
  amendmentReason: null,
  amendedAt: null,
  amendmentCount: 0,
  version: 1,
};

const BILL: PortalBillDetail = {
  id: BILL_ID,
  billNumber: 'OP2600001',
  type: 'OutPatient',
  status: 'Paid',
  patientId: PATIENT_ID,
  patientName: 'Asha Verma',
  billedAt: '2026-09-27T06:00:00Z',
  grossAmount: 500,
  discountAmount: 0,
  taxAmount: 0,
  roundOffAmount: 0,
  netAmount: 500,
  payerAmount: 0,
  patientAmount: 500,
  paidAmount: 500,
  dueAmount: 0,
  lines: [
    {
      description: 'Consultation',
      quantity: 1,
      unitPrice: 500,
      discountAmount: 0,
      taxAmount: 0,
      netAmount: 500,
      serviceDate: '2026-09-27T06:00:00Z',
      isReturn: false,
    },
  ],
  receipts: [
    {
      id: '3a4b5c6d-7e8f-4a9b-8c1d-2e3f4a5b6c7d',
      receiptNumber: 'RC2600001',
      purpose: 'BillPayment',
      amount: 500,
      modes: 'UPI',
      receivedAt: '2026-09-27T06:30:00Z',
    },
  ],
};

/** The PDF of each document, with the file name the server gives it. */
const PDF_NAMES: Record<string, string> = {
  [`/api/v1/portal/reports/lab/${LAB_ID}/pdf`]: 'lab-report-LAB2600123.pdf',
  [`/api/v1/portal/reports/radiology/${SCAN_ID}/pdf`]: 'radiology-report-RAD2600045.pdf',
  [`/api/v1/portal/reports/cardiology/${HEART_ID}/pdf`]: 'cardiology-report-CAR2600007.pdf',
  [`/api/v1/portal/prescriptions/${RX_ID}/pdf`]: 'prescription-RX2600123.pdf',
  [`/api/v1/portal/discharge-summaries/${SUMMARY_ID}/pdf`]: 'discharge-summary-NH26000123-2026-09-25.pdf',
  [BILL_PDF]: 'bill-OP2600001.pdf',
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

/** An error as the server sends it (RFC 9457 problem details). */
function problem(status: number, body: Record<string, unknown>): Response {
  return new Response(JSON.stringify({ status, ...body }), {
    status,
    headers: { 'content-type': 'application/problem+json' },
  });
}

function pdf(fileName: string): Response {
  return new Response('%PDF-1.7\n', {
    status: 200,
    headers: {
      'content-type': 'application/pdf',
      'content-disposition': `attachment; filename=${fileName}; filename*=UTF-8''${fileName}`,
    },
  });
}

const calls: { method: string; path: string; language: string | null }[] = [];
let pdfAnswer: (fileName: string) => Response | Promise<Response>;
let saved: SavedFile[];

const fakeServer = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
  const method = (init?.method ?? 'GET').toUpperCase();
  const { pathname } = new URL(raw, 'https://portal.test');
  calls.push({ method, path: pathname, language: new Headers(init?.headers).get('X-HC360-Language') });
  const pdfName = PDF_NAMES[pathname];
  if (method === 'GET' && pdfName) {
    return pdfAnswer(pdfName);
  }
  switch (`${method} ${pathname}`) {
    case 'GET /api/v1/system/info':
      return json({ facilityName: 'Nana Hospitals' });
    case 'GET /api/v1/portal/profiles':
      return json([PROFILE]);
    case `GET /api/v1/portal/reports/lab/${LAB_ID}`:
      return json(LAB);
    case `GET /api/v1/portal/reports/radiology/${SCAN_ID}`:
      return json(SCAN);
    case `GET /api/v1/portal/reports/cardiology/${HEART_ID}`:
      return json(HEART);
    case `GET /api/v1/portal/prescriptions/${RX_ID}`:
      return json(RX);
    case `GET /api/v1/portal/prescriptions/${RX_AWAITING_DSC_ID}`:
      return json(RX_AWAITING_DSC);
    case 'GET /api/v1/portal/refills':
      return json({ prescriptions: [], requests: [] });
    case `GET /api/v1/portal/discharge-summaries/${SUMMARY_ID}`:
      return json(SUMMARY);
    case `GET /api/v1/portal/bills/${BILL_ID}`:
      return json(BILL);
    default:
      return json({ title: 'Not found', status: 404 }, 404);
  }
});

beforeAll(() => {
  window.matchMedia ??= ((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  })) as typeof window.matchMedia;
  window.scrollTo = (() => undefined) as typeof window.scrollTo;
});

beforeEach(() => {
  calls.length = 0;
  pdfAnswer = pdf;
  useProfileStore.getState().select(null);
  vi.stubGlobal('fetch', fakeServer);
  saved = catchDownloads();
  // As after a successful sign-in with a code: the access token lives in memory only.
  useSession.getState().setAuth({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', user: USER });
});

afterEach(() => {
  // The session is left as it is (see modules/privacy/pages.test.tsx): every test signs in again.
  useLanguageStore.getState().setLanguage('en');
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  sessionStorage.clear();
});

function openAt(path: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const history = createMemoryHistory({ initialEntries: [path] });
  render(
    <QueryClientProvider client={queryClient}>
      <FluentProvider theme={webLightTheme}>
        <RouterProvider router={createPortalRouter(queryClient, history)} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return history;
}

const sent = (method: string, path: string) => calls.filter((call) => call.method === method && call.path === path);

/** Toasts are also read out through a live region, so their text can be on the page twice for a moment. */
async function findToast(text: string | RegExp) {
  return (await screen.findAllByText(text, {}, SLOW))[0];
}

const PAGES = [
  {
    document: 'a lab report',
    path: `/portal/reports/lab/${LAB_ID}`,
    heading: 'Haemoglobin',
    pdf: `/api/v1/portal/reports/lab/${LAB_ID}/pdf`,
  },
  {
    document: 'a scan report',
    path: `/portal/reports/radiology/${SCAN_ID}`,
    heading: 'Chest X-ray',
    pdf: `/api/v1/portal/reports/radiology/${SCAN_ID}/pdf`,
  },
  {
    document: 'a heart test report',
    path: `/portal/reports/cardiology/${HEART_ID}`,
    heading: 'Resting ECG',
    pdf: `/api/v1/portal/reports/cardiology/${HEART_ID}/pdf`,
  },
  {
    document: 'a prescription',
    path: `/portal/prescriptions/${RX_ID}`,
    heading: 'Prescription',
    pdf: `/api/v1/portal/prescriptions/${RX_ID}/pdf`,
  },
  {
    document: 'a discharge summary',
    path: `/portal/records/discharge/${SUMMARY_ID}`,
    heading: 'Discharge summary',
    pdf: `/api/v1/portal/discharge-summaries/${SUMMARY_ID}/pdf`,
  },
  { document: 'a bill', path: `/portal/bills/${BILL_ID}`, heading: 'Bill no. OP2600001', pdf: BILL_PDF },
];

describe('Download PDF', () => {
  it.each(PAGES)('saves the hospital PDF of $document under the name the server gives it', async (page) => {
    const user = userEvent.setup();
    openAt(page.path);
    expect(await screen.findByRole('heading', { name: page.heading, level: 1 }, SLOW)).toBeInTheDocument();
    // The real PDF replaces the HTML copy and the "Save as PDF" tip; the page's own copy can still be printed.
    expect(screen.getByText(HINT)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Print' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Download a copy' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Print or save as PDF' })).not.toBeInTheDocument();
    expect(screen.queryByText(/choose "Save as PDF"/)).not.toBeInTheDocument();
    const download = screen.getByRole('button', { name: 'Download PDF' });
    expect(download).toHaveAccessibleDescription(HINT);

    await user.click(download);
    expect(await findToast('The PDF was saved to your downloads.')).toBeInTheDocument();
    expect(saved.map((file) => file.name)).toEqual([PDF_NAMES[page.pdf]]);
    expect(saved[0]?.blob.type).toBe('application/pdf');
    expect(sent('GET', page.pdf)).toHaveLength(1);
  });

  it('shows that the PDF is being made, and takes one tap at a time', async () => {
    let release: () => void = () => undefined;
    const ready = new Promise<void>((resolve) => {
      release = resolve;
    });
    pdfAnswer = async (fileName) => {
      await ready;
      return pdf(fileName);
    };
    const user = userEvent.setup();
    openAt(`/portal/bills/${BILL_ID}`);
    await user.click(await screen.findByRole('button', { name: 'Download PDF' }, SLOW));
    const busy = await screen.findByRole('button', { name: 'Preparing your PDF…' }, SLOW);
    expect(busy).toHaveAttribute('aria-disabled', 'true');
    await user.click(busy);

    release();
    expect(await findToast('The PDF was saved to your downloads.')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Download PDF' }, SLOW)).not.toHaveAttribute(
      'aria-disabled',
      'true',
    );
    expect(saved.map((file) => file.name)).toEqual(['bill-OP2600001.pdf']);
    expect(sent('GET', BILL_PDF)).toHaveLength(1);
  });

  it('says kindly when the PDF cannot be made right now, and offers printing instead', async () => {
    pdfAnswer = () =>
      problem(503, {
        title: 'The PDF could not be made',
        detail: 'Microsoft Edge was not found on the server.',
        code: 'pdf.unavailable',
      });
    const user = userEvent.setup();
    openAt(`/portal/bills/${BILL_ID}`);
    await user.click(await screen.findByRole('button', { name: 'Download PDF' }, SLOW));
    expect(await screen.findByText('We cannot make the PDF right now', {}, SLOW)).toBeInTheDocument();
    expect(
      screen.getByText('Please try again in a little while. You can print this page now instead.'),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Print' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Download PDF' })).not.toHaveAttribute('aria-disabled', 'true');
    expect(screen.queryByText(HINT)).not.toBeInTheDocument();
    // The server's reason is for the hospital's IT staff, and it is in English.
    expect(screen.queryByText(/Microsoft Edge/)).not.toBeInTheDocument();
    expect(saved).toEqual([]);
    // A "cannot make it now" answer is final: the page does not keep asking.
    expect(sent('GET', BILL_PDF)).toHaveLength(1);
  });

  it('reports other failures the usual way', async () => {
    pdfAnswer = () => problem(404, { title: 'Not found', detail: `Bill '${BILL_ID}' was not found.` });
    const user = userEvent.setup();
    openAt(`/portal/bills/${BILL_ID}`);
    await user.click(await screen.findByRole('button', { name: 'Download PDF' }, SLOW));
    expect(await findToast('Could not download the PDF')).toBeInTheDocument();
    expect(
      await findToast('We could not find this. It may have been removed, or the link may be old.'),
    ).toBeInTheDocument();
    expect(screen.queryByText('We cannot make the PDF right now')).not.toBeInTheDocument();
    expect(saved).toEqual([]);
  });

  it('keeps printing receipts from the page', async () => {
    openAt(`/portal/bills/${BILL_ID}`);
    expect(await screen.findByRole('button', { name: 'Print receipt' }, SLOW)).toBeInTheDocument();
  });

  it('speaks Hindi, prints the Hindi copy, and asks the server for the PDF in Hindi', async () => {
    useLanguageStore.getState().setLanguage('hi');
    const print = vi.spyOn(window, 'print').mockImplementation(() => undefined);
    const user = userEvent.setup();
    openAt(`/portal/prescriptions/${RX_ID}`);
    expect(await screen.findByRole('button', { name: 'PDF डाउनलोड करें' }, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/अस्पताल की आधिकारिक प्रति मिलती है।/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'प्रिंट करें' }));
    expect(print).toHaveBeenCalledTimes(1);
    expect(document.getElementById('hc360-print-root')?.innerHTML).toContain('दवा का पर्चा');
    expect(calls.some((call) => call.path.endsWith('/pdf'))).toBe(false);
    // The dialog closes, as a browser says it has: the copy leaves the page, and nothing waits on it after this test.
    window.dispatchEvent(new Event('afterprint'));
    expect(document.getElementById('hc360-print-root')?.innerHTML).toBe('');

    // The server writes the PDF in the language chosen here, whatever the patient's record prefers.
    await user.click(screen.getByRole('button', { name: 'PDF डाउनलोड करें' }));
    expect(await findToast('PDF आपके डाउनलोड में सहेज दिया गया है।')).toBeInTheDocument();
    expect(saved).toHaveLength(1);
    expect(calls.find((call) => call.path.endsWith('/pdf'))?.language).toBe('hi');
  });
});

describe('a prescription the doctor has not signed with their DSC yet (ERX-04)', () => {
  const PENDING =
    'Your doctor has not digitally signed this prescription yet. You can print it, download it and show its QR code at a pharmacy once they have.';

  it('offers no print, PDF or QR code, and says why', async () => {
    openAt(`/portal/prescriptions/${RX_AWAITING_DSC_ID}`);
    expect(await screen.findByText('Not ready to print yet', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(PENDING)).toBeInTheDocument();
    // The medicines are still listed on the page (an earlier test's print copy may still be in the document too).
    expect(screen.getAllByText('Paracetamol 500 mg Tablet').some((line) => !line.closest('#hc360-print-root'))).toBe(
      true,
    );
    expect(screen.queryByRole('button', { name: 'Download PDF' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Print' })).not.toBeInTheDocument();
    expect(screen.queryByText('Show this at the pharmacy')).not.toBeInTheDocument();
    expect(calls.some((call) => call.path.endsWith('/pdf'))).toBe(false);
  });

  it('says so in Hindi too', async () => {
    useLanguageStore.getState().setLanguage('hi');
    openAt(`/portal/prescriptions/${RX_AWAITING_DSC_ID}`);
    expect(await screen.findByText('अभी प्रिंट के लिए तैयार नहीं', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/आपके डॉक्टर ने अभी इस पर्चे पर डिजिटल हस्ताक्षर नहीं किए हैं/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'PDF डाउनलोड करें' })).not.toBeInTheDocument();
  });

  it("words the server's refusal of the PDF for the patient, by its code", async () => {
    // A page opened before the hospital began to require the DSC still offers the PDF; the server refuses it.
    pdfAnswer = () =>
      problem(422, {
        title: 'Not issued yet',
        detail: 'Your doctor has not digitally signed this prescription yet.',
        code: 'prescription.digital_signature_required',
      });
    useLanguageStore.getState().setLanguage('hi');
    const user = userEvent.setup();
    openAt(`/portal/prescriptions/${RX_ID}`);
    await user.click(await screen.findByRole('button', { name: 'PDF डाउनलोड करें' }, SLOW));
    expect(await findToast(/आपके डॉक्टर ने अभी इस पर्चे पर डिजिटल हस्ताक्षर नहीं किए हैं/)).toBeInTheDocument();
    expect(screen.queryByText('Your doctor has not digitally signed this prescription yet.')).not.toBeInTheDocument();
    expect(saved).toEqual([]);
  });
});

describe('documents the server does not make (the privacy summary)', () => {
  it('keep "Print or save as PDF" and the HTML copy, in the language being read', async () => {
    const user = userEvent.setup();
    render(
      <FluentProvider theme={webLightTheme}>
        <DocumentActions
          build={() => ({ title: 'The data we hold', body: '<h1>The data we hold</h1>', fileName: 'privacy-summary' })}
        />
      </FluentProvider>,
    );
    expect(screen.getByRole('button', { name: 'Print or save as PDF' })).toBeInTheDocument();
    expect(screen.getByText('To save a PDF, choose "Save as PDF" as the printer.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Download PDF' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Download a copy' }));
    expect(saved.map((file) => file.name)).toEqual(['privacy-summary.html']);
    expect(await saved[0]?.blob.text()).toContain('<html lang="en-IN">');
    expect(calls).toEqual([]);
  });
});
