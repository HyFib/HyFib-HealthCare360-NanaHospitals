import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AbdmLinkView } from '@/modules/emr/abdmLinking';
import { stubBrowserApis } from '@/test/appScreen';
import { AbdmLinkRequestsPanel } from './AbdmLinkRequestsPanel';
import type { PatientDetail } from './api';

// The records sent to the patient's ABHA, by the hospital as they are completed or by staff, and "Link again" on a
// request ABDM refused or never answered. The server is replaced.

const get = vi.hoisted(() => vi.fn());
const post = vi.hoisted(() => vi.fn());
vi.mock('@/lib/api', () => ({ api: { get, post } }));
const state = vi.hoisted(() => ({ link: true, abdm: true }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (permission: string) => permission !== 'abdm.export' || state.link,
}));
vi.mock('@/lib/features', () => ({ useFeatureOn: () => state.abdm }));
const notify = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<object>()), useNotify: () => notify }));

const PATIENT = { id: 'p1', fullName: 'Meera Iyer', abhaAddress: 'meera.iyer@sbx' } as unknown as PatientDetail;

function link(patch: Partial<AbdmLinkView> = {}): AbdmLinkView {
  return {
    id: 'l1',
    patientId: 'p1',
    abhaAddress: 'meera.iyer@sbx',
    status: 'Linked',
    records: [{ type: 'LabReport', id: 'r1', referenceNumber: 'lab-report/r1', display: 'Lab report, 28 Oct 2026' }],
    createdAt: '2026-10-28T10:00:00Z',
    sentAt: '2026-10-28T10:00:00Z',
    answeredAt: '2026-10-28T10:00:05Z',
    errorCode: null,
    error: null,
    automatic: true,
    retryable: false,
    ...patch,
  };
}

const NEVER_ANSWERED = link({
  id: 'l2',
  status: 'Failed',
  automatic: false,
  records: [
    { type: 'Prescription', id: 'r2', referenceNumber: 'prescription/r2', display: 'Prescription, 27 Oct 2026' },
  ],
  errorCode: 'abdm.no_answer',
  error:
    'ABDM did not send a link token within 15 minutes. Link the records again; if ABDM answers later, its answer is still taken.',
  retryable: true,
});

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <AbdmLinkRequestsPanel patient={PATIENT} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

const row = async (text: string) => (await screen.findByText(text)).closest('tr')!;

describe('AbdmLinkRequestsPanel', () => {
  beforeEach(() => {
    stubBrowserApis();
    state.link = true;
    state.abdm = true;
    get.mockReset().mockResolvedValue([NEVER_ANSWERED, link()]);
    post.mockReset();
    Object.values(notify).forEach((fn) => fn.mockReset());
  });

  it('lists the patient’s requests, who made them, where they have got to and why ABDM did not link one', async () => {
    renderPanel();

    expect(await screen.findByText("Linked to the patient's ABHA")).toBeInTheDocument();
    const linked = await row('Lab report, 28 Oct 2026');
    expect(within(linked).getByText('Automatically')).toBeInTheDocument();
    expect(within(linked).getByText('Linked')).toBeInTheDocument();
    expect(within(linked).queryByRole('button', { name: 'Link again' })).not.toBeInTheDocument();

    const failed = await row('Prescription, 27 Oct 2026');
    expect(within(failed).getByText('By staff')).toBeInTheDocument();
    expect(within(failed).getByText('Not linked')).toBeInTheDocument();
    expect(within(failed).getByText(/did not send a link token within 15 minutes/)).toBeInTheDocument();
    expect(get).toHaveBeenCalledWith('/api/v1/abdm/patients/p1/links', expect.anything());
  });

  it('links a failed request again as a new request, and lists it', async () => {
    post.mockResolvedValue(link({ id: 'l3', status: 'AwaitingToken', automatic: false, answeredAt: null }));
    renderPanel();
    const user = userEvent.setup();

    await user.click(within(await row('Prescription, 27 Oct 2026')).getByRole('button', { name: 'Link again' }));

    await waitFor(() => expect(post).toHaveBeenCalledTimes(1));
    const [path, body, options] = post.mock.calls[0]!;
    expect(path).toBe('/api/v1/abdm/links/l2/retry');
    expect(body).toBeUndefined();
    expect(options.idempotencyKey).toEqual(expect.any(String));
    expect(notify.info).toHaveBeenCalledWith('Sent to ABDM again', expect.stringContaining('meera.iyer@sbx'));
    await waitFor(() => expect(get).toHaveBeenCalledTimes(2));
  });

  it('sends the same request again, under the same key, when the server’s answer was lost', async () => {
    post
      .mockRejectedValueOnce(new ApiError({ status: 0, title: 'Cannot reach the HealthCare360 server' }))
      .mockResolvedValue(link({ id: 'l3' }));
    renderPanel();
    const user = userEvent.setup();
    const again = within(await row('Prescription, 27 Oct 2026')).getByRole('button', { name: 'Link again' });

    await user.click(again);
    await waitFor(() => expect(notify.error).toHaveBeenCalled());
    await user.click(again);

    await waitFor(() => expect(post).toHaveBeenCalledTimes(2));
    const [first, second] = post.mock.calls;
    expect(second![2].idempotencyKey).toBe(first![2].idempotencyKey);
  });

  it('is not shown without the permission to link records, or while ABDM is off', () => {
    state.link = false;
    renderPanel();
    expect(screen.queryByText("Linked to the patient's ABHA")).not.toBeInTheDocument();
    expect(get).not.toHaveBeenCalled();
  });
});
