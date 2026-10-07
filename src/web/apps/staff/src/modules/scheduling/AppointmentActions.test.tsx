import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as SchedulingApi from './api';
import { schedulingApi, type AppointmentRef, type CheckInResult } from './api';
import { AppointmentRowActions } from './AppointmentActions';
import { useAppointmentDesk } from './useAppointmentDesk';

// The server, the router, permissions and toasts are outside the test; the row's buttons and the dialogs they open
// are what is tested.
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof SchedulingApi>()),
  schedulingApi: { checkIn: vi.fn(), cancel: vi.fn(), confirm: vi.fn(), noShow: vi.fn(), reschedule: vi.fn() },
}));
vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
vi.mock('@tanstack/react-router', () => ({ useNavigate: () => vi.fn() }));

const startsAt = new Date(Date.now() + 30 * 60_000).toISOString();
const appointment: AppointmentRef = {
  id: 'a1',
  number: 'NHAP260000123',
  patientId: 'p1',
  uhid: 'NH0001234',
  patientName: 'Ravi Kumar Sharma',
  practitionerId: 'd1',
  practitionerName: 'Dr Anil Rao',
  departmentId: 'dep1',
  departmentName: 'General Medicine',
  startsAt,
  endsAt: new Date(Date.parse(startsAt) + 15 * 60_000).toISOString(),
  status: 'Booked',
  channel: 'WalkIn',
  visitType: 'New',
  isTelehealth: false,
  paymentStatus: 'PayAtHospital',
  feeAmount: 500,
  reason: 'Fever for three days',
  room: '12',
  visitId: null,
};

function Row() {
  const desk = useAppointmentDesk();
  return (
    <>
      <AppointmentRowActions appointment={appointment} desk={desk} />
      {desk.dialogs}
    </>
  );
}

function renderRow() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <Row />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('AppointmentRowActions', () => {
  beforeEach(() => {
    Object.values(schedulingApi).forEach((fn) => vi.mocked(fn).mockReset());
  });

  it('returns focus to the row menu button when a dialog opened from it is closed from the keyboard', async () => {
    renderRow();
    const more = screen.getByRole('button', { name: 'More actions for Ravi Kumar Sharma' });

    more.focus();
    await userEvent.keyboard('{Enter}');
    await waitFor(() => expect(screen.getAllByRole('menuitem')[0]).toHaveFocus());
    await userEvent.keyboard('{End}');
    expect(screen.getByRole('menuitem', { name: 'Cancel appointment' })).toHaveFocus();
    await userEvent.keyboard('{Enter}');
    const dialog = await screen.findByRole('alertdialog');
    await waitFor(() => expect(dialog).toContainElement(document.activeElement as HTMLElement));
    await userEvent.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    await waitFor(() => expect(more).toHaveFocus());
  });

  it('returns focus to the row Check in button when the check-in is closed from the keyboard', async () => {
    renderRow();
    const checkIn = screen.getByRole('button', { name: 'Check in' });

    checkIn.focus();
    await userEvent.keyboard('{Enter}');
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(dialog).toContainElement(document.activeElement as HTMLElement));
    await userEvent.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(checkIn).toHaveFocus());
  });

  it('keeps focus on Check in while the patient is checked in, and checks in only once', async () => {
    vi.mocked(schedulingApi.checkIn).mockReturnValue(new Promise<CheckInResult>(() => {}));
    renderRow();

    await userEvent.click(screen.getByRole('button', { name: 'Check in' }));
    const dialog = await screen.findByRole('dialog');
    const confirm = within(dialog).getByRole('button', { name: 'Check in' });
    await userEvent.click(confirm);

    // A disabled button loses focus in the browser, and focus that leaves the dialog goes back to the row behind it.
    await waitFor(() => expect(confirm).toHaveAttribute('aria-disabled', 'true'));
    expect(confirm).not.toBeDisabled();
    expect(confirm).toHaveFocus();
    await userEvent.keyboard('{Enter}');
    await userEvent.keyboard('{Escape}');
    expect(schedulingApi.checkIn).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('dialog')).toBe(dialog);
  });
});
