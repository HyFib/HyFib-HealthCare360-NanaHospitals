import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { stubBrowserApis } from '@/test/appScreen';
import { AbdmConsentRequestsPanel, type AbdmConsentRequest } from './AbdmConsentRequestsPanel';
import type { PatientDetail } from './api';

// Asking the patient, through ABDM, to share their records at other providers: the doctor's request, its status, and
// the providers a granted request covers. The server is replaced.

const get = vi.hoisted(() => vi.fn());
const post = vi.hoisted(() => vi.fn());
vi.mock('@/lib/api', () => ({ api: { get, post } }));
const state = vi.hoisted(() => ({ request: true, abdm: true }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (permission: string) => permission !== 'abdm.records.request' || state.request,
}));
vi.mock('@/lib/features', () => ({ useFeatureOn: () => state.abdm }));
const notify = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<object>()), useNotify: () => notify }));

const PATIENT = {
  id: 'p1',
  fullName: 'Kiran Rao',
  abhaAddress: 'kiran.rao@abdm',
  mergedIntoPatientId: null,
} as unknown as PatientDetail;

function request(patch: Partial<AbdmConsentRequest> = {}): AbdmConsentRequest {
  return {
    id: 'r1',
    patientId: 'p1',
    status: 'Requested',
    purpose: 'CAREMGT',
    hiTypes: ['OPConsultation', 'DiagnosticReport'],
    dateFrom: '2024-01-01T00:00:00Z',
    dateTo: '2026-10-01T23:59:59Z',
    dataEraseAt: '2026-11-25T23:59:59Z',
    requesterName: 'Dr. Meera Iyer',
    requestedAt: '2026-10-26T10:00:00Z',
    decidedAt: null,
    error: null,
    artefacts: [],
    ...patch,
  };
}

function renderPanel(patient: PatientDetail = PATIENT) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <AbdmConsentRequestsPanel patient={patient} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('AbdmConsentRequestsPanel', () => {
  beforeEach(() => {
    stubBrowserApis();
    state.request = true;
    state.abdm = true;
    get.mockReset().mockResolvedValue([]);
    post.mockReset();
    notify.success.mockReset();
  });

  it('sends the request with the kinds of records, dates and purpose chosen', async () => {
    post.mockResolvedValue(request({ status: 'Sent' }));
    renderPanel();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Ask for records through ABDM' }));
    await user.click(await screen.findByRole('checkbox', { name: 'Prescriptions' }));
    await user.click(screen.getByRole('checkbox', { name: 'Immunisations' }));
    await user.click(screen.getByRole('button', { name: 'Send request' }));

    await waitFor(() => expect(post).toHaveBeenCalled());
    const [path, body] = post.mock.calls[0]!;
    expect(path).toBe('/api/v1/abdm/patients/p1/consent-requests');
    expect(body).toMatchObject({
      purpose: 'CAREMGT',
      hiTypes: ['OPConsultation', 'DischargeSummary', 'DiagnosticReport', 'ImmunizationRecord'],
    });
    expect(body.from).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Request sent', 'The patient decides in their ABHA app.'),
    );
  });

  it('shows a request the consent manager refused in the dialog', async () => {
    post.mockResolvedValue(
      request({ status: 'Failed', error: 'The ABDM gateway refused to ask for the patient’s consent.' }),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Ask for records through ABDM' }));
    await user.click(screen.getByRole('button', { name: 'Send request' }));
    expect(await screen.findByText(/refused to ask for the patient/)).toBeInTheDocument();
    expect(notify.success).not.toHaveBeenCalled();
  });

  it('lists requests with the providers a granted one covers', async () => {
    get.mockResolvedValue([
      request({
        status: 'Granted',
        decidedAt: '2026-10-26T11:00:00Z',
        artefacts: [
          {
            artefactId: 'a1',
            status: 'GRANTED',
            hipId: 'IN29',
            hipName: 'City Care Hospital',
            records: 2,
            fetchedAt: '2026-10-26T11:01:00Z',
          },
          { artefactId: 'a2', status: 'REQUESTED', hipId: null, hipName: null, records: 0, fetchedAt: null },
        ],
      }),
    ]);
    renderPanel();
    expect(await screen.findByText('Granted')).toBeInTheDocument();
    expect(screen.getByText('City Care Hospital: 2 records')).toBeInTheDocument();
    expect(screen.getByText('A provider: details awaited')).toBeInTheDocument();
    expect(screen.getByText('Consultations, Lab and imaging reports')).toBeInTheDocument();
  });

  it('asks for an ABHA address first, and is not shown without the permission', async () => {
    renderPanel({ ...PATIENT, abhaAddress: null } as PatientDetail);
    expect(await screen.findByText(/Add the patient's ABHA address/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ask for records through ABDM' })).not.toBeInTheDocument();
  });

  it('is not shown without the permission or while ABDM is off', () => {
    state.request = false;
    renderPanel();
    expect(screen.queryByText(/Ask the patient to let this hospital/)).not.toBeInTheDocument();
    expect(get).not.toHaveBeenCalled();
  });
});
