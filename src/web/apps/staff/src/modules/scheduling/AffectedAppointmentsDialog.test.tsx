import { useSession } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { AffectedAppointmentsDialog } from './AffectedAppointmentsDialog';
import type { AffectedAppointment, StrandedPatients } from './api';

// A doctor's leave strands bookings: the desk tells every patient at once, by SMS and WhatsApp, with a link to choose
// another time, and the dialog lists who was told and how. Nothing is rebooked for them. The server is a fake.

const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const EXCEPTION_ID = '0192a000-0000-7000-8000-00000000e101';
const ASHA: AffectedAppointment = {
  id: '0192a000-0000-7000-8000-00000000e201',
  number: 'APT-2026-000311',
  patientId: '0192a000-0000-7000-8000-000000000001',
  patientName: 'Asha Devi',
  phoneMasked: '98XXXXXX10',
  startsAt: '2026-11-17T04:30:00Z',
  status: 'Booked',
};

const stranded = (patch: Partial<StrandedPatients>, toldAt: string | null): StrandedPatients => ({
  exceptionId: EXCEPTION_ID,
  smsReady: true,
  smsNote: null,
  patients: [
    {
      appointmentId: ASHA.id,
      number: ASHA.number,
      patientId: ASHA.patientId,
      patientName: ASHA.patientName,
      phoneMasked: ASHA.phoneMasked,
      startsAt: ASHA.startsAt,
      status: 'Booked',
      toldAt,
      notices: toldAt
        ? [
            { channel: 'Sms', status: 'Queued', error: null },
            { channel: 'WhatsApp', status: 'Suppressed', error: 'The number replied STOP' },
          ]
        : [],
      notToldReason: null,
    },
  ],
  ...patch,
});

let server: ReturnType<typeof fakeServer>;

function showDialog(before: StrandedPatients) {
  server = fakeServer({
    [`GET /api/v1/scheduling/exceptions/${EXCEPTION_ID}/stranded`]: before,
    [`POST /api/v1/scheduling/exceptions/${EXCEPTION_ID}/notify-patients`]: stranded({}, '2026-11-08T05:12:00Z'),
  });
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 3600_000,
    user: STAFF_USER,
    locked: false,
  });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <Hc360ThemeProvider>
        <AffectedAppointmentsDialog affected={[ASHA]} exceptionId={EXCEPTION_ID} onClose={vi.fn()} />
      </Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('telling the patients a leave strands', () => {
  it('tells them in one action and lists who was told, on which channel and what went wrong', async () => {
    const user = userEvent.setup();
    showDialog(stranded({}, null));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText('Not told yet')).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Tell the patients by SMS/WhatsApp' }));

    expect(await within(dialog).findByText(/^Told .* · SMS, WhatsApp$/)).toBeInTheDocument();
    expect(within(dialog).getByText('WhatsApp: suppressed (The number replied STOP)')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Patients told' })).toBeDisabled();
    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Patients told', expect.stringContaining('1 patient was told')),
    );
    expect(server.requests.filter((r) => r.method === 'POST')).toHaveLength(1);
  });

  it('says when the SMS cannot go for want of a DLT template id', async () => {
    showDialog(stranded({ smsReady: false, smsNote: 'The SMS template has no DLT template id.' }, null));

    expect(await screen.findByText('The SMS template has no DLT template id.')).toBeInTheDocument();
    await expectNoAxeViolations(document.body);
  });

  it('has no accessibility problems', async () => {
    const { baseElement } = showDialog(stranded({}, null));
    await screen.findByText('Not told yet');
    await expectNoAxeViolations(baseElement);
  });
});
