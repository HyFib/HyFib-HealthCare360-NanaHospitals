import type * as Core from '@hc360/core';
import { useSession, type AuthUser } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, json, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { EncashableLeaveDto } from './api';
import { EncashLeaveDialog } from './LeaveEncashment';

// Leave encashed while in service, on the employee's leave screen against a fake server: HR picks a leave type the policy
// pays out in service, sees the cap, the days kept and roughly what it comes to, and asks for approval; a refusal by the
// policy is explained. The toasts are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const EMPLOYEE_ID = '0192a000-0000-7000-8000-00000000c101';
const EL_ID = '0192a000-0000-7000-8000-00000000c102';
const EMPLOYEE = { id: EMPLOYEE_ID, code: 'NHEMP00051', name: 'Revathi Sundaram' };

const HR: AuthUser = {
  ...STAFF_USER,
  displayName: 'Kavitha Iyer',
  roles: ['HrManager'],
  permissions: ['hr.employees.read', 'hr.payroll.process'],
};

const EARNED_LEAVE: EncashableLeaveDto = {
  leaveTypeId: EL_ID,
  leaveTypeCode: 'EL',
  leaveTypeName: 'Earned Leave',
  year: 2026,
  capPerYear: 10,
  minimumBalance: 15,
  encashedThisYear: 4,
  waiting: 0,
  mostDays: 6,
  dailyRate: 1200,
};

const refused = (status: number, code: string, detail: string) =>
  new Response(JSON.stringify({ title: 'Refused', status, code, detail }), {
    status,
    headers: { 'content-type': 'application/problem+json' },
  });

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function show(node: ReactNode) {
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 3600_000,
    user: HR,
    locked: false,
  });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <Hc360ThemeProvider>{node}</Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

describe('leave encashed while in service', SCREEN_TESTS, () => {
  it('shows the policy for the leave chosen, explains a refusal, and asks for approval', async () => {
    let overCap = true;
    const server = fakeServer({
      'GET /api/v1/hr/leave/encashments/encashable': json([EARNED_LEAVE]),
      'POST /api/v1/hr/leave/encashments': () =>
        overCap
          ? refused(422, 'leave.encash_over_cap', 'The policy pays out at most 10 day(s) of EL a year in service.')
          : json(
              {
                id: '0192a000-0000-7000-8000-00000000c1a1',
                kind: 'hr.leave_encash',
                summary: '5 day(s) of EL 2026 encashed for Revathi Sundaram (NHEMP00051): ₹6,000.00',
                status: 'Pending',
                canDecide: false,
              },
              202,
            ),
    });
    const user = userEvent.setup();
    show(<EncashLeaveDialog open onClose={() => undefined} employee={EMPLOYEE} year={2026} />);

    const dialog = await screen.findByRole('dialog', {}, SLOW);
    await user.selectOptions(await within(dialog).findByRole('combobox', { name: /Leave type/ }), EL_ID);
    expect(within(dialog).getByText(/at most 10 day\(s\) a year in service and keeps 15/)).toBeInTheDocument();
    expect(within(dialog).getByText(/4 encashed this year, 0 waiting, so at most 6 now/)).toBeInTheDocument();

    const days = within(dialog).getByRole('spinbutton', { name: /^Days/ });
    await user.clear(days);
    await user.type(days, '7');
    await user.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Asked for by the employee');
    expect(within(dialog).getByText(/never you and never Revathi Sundaram/)).toBeInTheDocument();
    expect(
      within(dialog).getByText(/taxable salary: s\.10\(10AA\) exempts leave encashment only on retirement or leaving/),
    ).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    expect(await within(dialog).findByText('Not asked for')).toBeInTheDocument();
    expect(within(dialog).getByText(/more than the leave policy pays out in service this year/)).toBeInTheDocument();
    await expectNoAxeViolations();

    overCap = false;
    await user.clear(days);
    await user.type(days, '5');
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));
    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining('approves payroll')),
    );
    const sent = server.requests.filter((r) => r.method === 'POST').at(-1)?.body as Record<string, unknown>;
    expect(sent).toMatchObject({
      employeeId: EMPLOYEE_ID,
      leaveTypeId: EL_ID,
      year: 2026,
      days: 5,
      reason: 'Asked for by the employee',
    });
  });

  it('says when no leave pays out in service, and asks for nothing', async () => {
    fakeServer({ 'GET /api/v1/hr/leave/encashments/encashable': json([]) });
    show(<EncashLeaveDialog open onClose={() => undefined} employee={EMPLOYEE} year={2026} />);

    const dialog = await screen.findByRole('dialog', {}, SLOW);
    expect(await within(dialog).findByText('No leave pays out in service')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Ask for approval' })).toBeDisabled();
    await expectNoAxeViolations();
  });
});
