import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createFakeFetch, problem, type FakeRequest } from '@/test/fakeApi';
import type { VisitDetail } from '@/modules/patients/api';
import { CheckInDialog } from './CheckInDialog';

// Check-in at the front desk (FO-03/04): a new OP visit with a queue token. The server is a fake answering the
// look-ups and the visit; the toasts and printing are outside the test.

const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
vi.mock('@/lib/print', () => ({ printTokenSlip: vi.fn(() => Promise.resolve()) }));

const PATIENT = { id: '0192a000-0000-7000-8000-000000000001', uhid: 'NH26000123', fullName: 'Asha Devi' };
const OPD = { id: '0192a000-0000-7000-8000-00000000de01', code: 'GM', name: 'General medicine' };
const DOCTOR = { id: '0192a000-0000-7000-8000-0000000000d2', code: 'D002', name: 'Dr. Anil Rao' };

const VISIT: VisitDetail = {
  id: '0192a000-0000-7000-8000-000000000c01',
  visitNumber: 'NHOV260000211',
  patientId: PATIENT.id,
  type: 'OutPatient',
  status: 'Open',
  departmentId: OPD.id,
  practitionerId: DOCTOR.id,
  appointmentId: null,
  visitedAt: '2026-10-23T04:00:00Z',
  completedAt: null,
  isFollowUp: false,
  isFreeFollowUp: false,
  previousVisitId: null,
  payerType: 'Self',
  payerId: null,
  referralSource: 'Self',
  referredBy: null,
  chiefComplaint: null,
  isMlc: false,
  mlcNumber: null,
  notes: null,
  cancelReason: null,
  token: {
    id: '0192a000-0000-7000-8000-000000000c11',
    visitId: '0192a000-0000-7000-8000-000000000c01',
    patientId: PATIENT.id,
    departmentId: OPD.id,
    practitionerId: DOCTOR.id,
    tokenDate: '2026-10-23',
    sequence: 14,
    label: 'GM-014',
    status: 'Waiting',
    priority: 'Normal',
    issuedAt: '2026-10-23T04:00:00Z',
    calledAt: null,
    serviceStartedAt: null,
    completedAt: null,
    room: null,
    callCount: 0,
  },
  version: 1,
};

let server: ReturnType<typeof createFakeFetch>;
let answerVisit: (request: FakeRequest) => unknown;

beforeEach(() => {
  vi.clearAllMocks();
  answerVisit = () => VISIT;
  server = createFakeFetch({
    'GET /api/v1/masters/lookups/departments': [OPD],
    'GET /api/v1/masters/lookups/practitioners': [DOCTOR],
    'POST /api/v1/visits': (request: FakeRequest) => answerVisit(request),
  });
  vi.stubGlobal('fetch', vi.fn(server.fetch));
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function openDialog() {
  const onClose = vi.fn();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <CheckInDialog patient={PATIENT} open onClose={onClose} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return { onClose };
}

const visitsSent = () => server.requests.filter((r) => r.method === 'POST' && r.path === '/api/v1/visits');

describe('check-in with a queue token', () => {
  it('needs the department', async () => {
    const user = userEvent.setup();
    openDialog();
    await user.click(screen.getByRole('button', { name: 'Check in and issue token' }));
    expect(await screen.findByText('Choose a department.')).toBeInTheDocument();
    expect(visitsSent()).toEqual([]);
  });

  it('creates an OP visit with a token for the department and doctor, and shows the token', async () => {
    const user = userEvent.setup();
    openDialog();
    const department = screen.getByRole('combobox', { name: /^Department/ });
    await waitFor(() => expect(department).toHaveTextContent('General medicine'));
    await user.selectOptions(department, OPD.id);
    const doctor = screen.getByRole('combobox', { name: /^Doctor/ });
    await waitFor(() => expect(doctor).toHaveTextContent('Dr. Anil Rao'));
    await user.selectOptions(doctor, DOCTOR.id);
    await user.click(screen.getByRole('button', { name: 'Check in and issue token' }));

    expect(await screen.findByText('GM-014')).toBeInTheDocument();
    expect(screen.getByText('Visit number NHOV260000211')).toBeInTheDocument();
    expect(visitsSent().map((r) => r.body)).toEqual([
      {
        patientId: PATIENT.id,
        type: 'OutPatient',
        departmentId: OPD.id,
        practitionerId: DOCTOR.id,
        appointmentId: null,
        payerType: 'Self',
        payerId: null,
        referralSource: 'Self',
        referredBy: null,
        chiefComplaint: null,
        isMlc: false,
        mlcNumber: null,
        notes: null,
        priority: 'Normal',
        issueToken: true,
        notifyPatient: true,
      },
    ]);
  });

  it('a medico-legal case needs its MLC number', async () => {
    const user = userEvent.setup();
    openDialog();
    const department = screen.getByRole('combobox', { name: /^Department/ });
    await waitFor(() => expect(department).toHaveTextContent('General medicine'));
    await user.selectOptions(department, OPD.id);
    await user.click(screen.getByRole('switch', { name: 'Medico-legal case (MLC)' }));
    await user.click(screen.getByRole('button', { name: 'Check in and issue token' }));
    expect(await screen.findByText('Enter the MLC number for a medico-legal case.')).toBeInTheDocument();
    expect(visitsSent()).toEqual([]);

    await user.type(screen.getByRole('textbox', { name: /^MLC number/ }), 'MLC/26/0042');
    await user.click(screen.getByRole('button', { name: 'Check in and issue token' }));
    await waitFor(() => expect(visitsSent()).toHaveLength(1));
    expect(visitsSent()[0]!.body).toMatchObject({ isMlc: true, mlcNumber: 'MLC/26/0042' });
  });

  it('tells the desk when the server refuses, and keeps what was chosen', async () => {
    const user = userEvent.setup();
    answerVisit = () => problem(422, 'The patient already has an open visit today');
    openDialog();
    const department = screen.getByRole('combobox', { name: /^Department/ });
    await waitFor(() => expect(department).toHaveTextContent('General medicine'));
    await user.selectOptions(department, OPD.id);
    await user.click(screen.getByRole('button', { name: 'Check in and issue token' }));

    await waitFor(() => expect(notify.error).toHaveBeenCalledWith(expect.anything(), 'Could not check the patient in'));
    expect(screen.queryByText('GM-014')).not.toBeInTheDocument();
    expect(department).toHaveValue(OPD.id);
  });
});
