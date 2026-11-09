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
import type { EmployeeDetail, PayrollInputDto, PendingPayChange, SalaryComponentDto } from './api';
import { BankDetailsDialog } from './BankDetailsDialog';
import { AdjustBalanceDialog } from './EmployeeLeave';
import { PayrollInputsPanel } from './PayrollInputs';

// Pay, payment details and leave on a second person's word (maker-checker, slice 11), on the HR screens against a fake
// server: a salary account is asked for with a reason and waits, what waits is shown, a deduction another module posted
// is asked to be taken off, and a refusal (one's own leave, a change already waiting) is explained. The toasts are outside
// the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const EMPLOYEE_ID = '0192a000-0000-7000-8000-00000000b101';
const RUN_ID = '0192a000-0000-7000-8000-00000000b102';
const LEAVE_TYPE_ID = '0192a000-0000-7000-8000-00000000b103';

const HR: AuthUser = {
  ...STAFF_USER,
  displayName: 'Kavitha Iyer',
  roles: ['HrManager'],
  permissions: ['hr.employees.read', 'hr.employees.manage', 'hr.payroll.process', 'hr.leave.approve'],
};

const EMPLOYEE = {
  id: EMPLOYEE_ID,
  employeeCode: 'NHEMP00042',
  fullName: 'Meena Raghavan',
  paymentMode: 'Cash',
  bankName: null,
  bankAccountMasked: null,
  bankIfscMasked: null,
  version: 7,
  pendingPaymentDetails: null,
} as unknown as EmployeeDetail;

const asked = (kind: string, summary: string) =>
  json({ id: '0192a000-0000-7000-8000-00000000b1a1', kind, summary, status: 'Pending', canDecide: false }, 202);

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

describe('pay and leave where a second person decides', SCREEN_TESTS, () => {
  it('asks for a salary account with a reason, and says the salary is paid as before until it is approved', async () => {
    const server = fakeServer({
      [`PUT /api/v1/hr/employees/${EMPLOYEE_ID}/bank-details`]: asked(
        'hr.bank_details',
        'Salary payment details of Meena Raghavan (NHEMP00042): cash → bank transfer to account XXXXXXXX7890',
      ),
    });
    const user = userEvent.setup();
    show(<BankDetailsDialog open onClose={() => undefined} employee={EMPLOYEE} />);

    const dialog = await screen.findByRole('dialog', {}, SLOW);
    expect(within(dialog).getByText(/never you and never Meena Raghavan/)).toBeInTheDocument();
    await user.selectOptions(within(dialog).getByRole('combobox', { name: /Salary paid by/ }), 'BankTransfer');
    await user.type(within(dialog).getByLabelText(/^Account number/), '001234567890');
    await user.type(within(dialog).getByLabelText(/^Type the account number again/), '001234567890');
    await user.type(within(dialog).getByRole('textbox', { name: /^IFSC/ }), 'SBIN0001234');

    // Not sent without a reason: the person who approves it reads it.
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));
    expect(await within(dialog).findByText(/Say why: the person who approves it reads it/)).toBeInTheDocument();
    expect(server.requests.some((r) => r.method === 'PUT')).toBe(false);

    await user.type(within(dialog).getByRole('textbox', { name: /^Reason for the change/ }), 'Opened a salary account');
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining('paid as before')),
    );
    expect(server.requests.find((r) => r.method === 'PUT')?.body).toEqual({
      paymentMode: 'BankTransfer',
      bankName: null,
      accountNumber: '001234567890',
      ifsc: 'SBIN0001234',
      confirmSharedAccount: false,
      reason: 'Opened a salary account',
      version: 7,
    });
  });

  it('shows what waits and explains a change already waiting for approval', async () => {
    fakeServer({
      [`PUT /api/v1/hr/employees/${EMPLOYEE_ID}/bank-details`]: refused(
        409,
        'approval.already_requested',
        'A request of this kind for this employee is already waiting for approval.',
      ),
    });
    const user = userEvent.setup();
    const waiting = {
      ...EMPLOYEE,
      pendingPaymentDetails: {
        approvalId: '0192a000-0000-7000-8000-00000000b1a2',
        paymentMode: 'Cheque',
        bankName: null,
        bankAccountMasked: null,
        bankIfscMasked: null,
        summary: 'Salary payment details of Meena Raghavan (NHEMP00042): cash → cheque',
        requestedBy: 'Hospital Administrator',
        requestedAt: '2026-11-08T05:00:00Z',
      },
    } as EmployeeDetail;
    show(<BankDetailsDialog open onClose={() => undefined} employee={waiting} />);

    const dialog = await screen.findByRole('dialog', {}, SLOW);
    expect(within(dialog).getByText('Already waiting for approval')).toBeInTheDocument();
    expect(within(dialog).getByText(/cash → cheque, asked for by Hospital Administrator/)).toBeInTheDocument();
    await user.selectOptions(within(dialog).getByRole('combobox', { name: /Salary paid by/ }), 'Cheque');
    await user.type(within(dialog).getByRole('textbox', { name: /^Reason for the change/ }), 'Wants a cheque');
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    expect(await within(dialog).findByText('Not asked for')).toBeInTheDocument();
    expect(within(dialog).getByText(/Withdraw it from Approvals first/)).toBeInTheDocument();
    await expectNoAxeViolations();
    expect(notify.success).not.toHaveBeenCalled();
  });

  it("lists a run's changes waiting for approval and asks before a module's deduction is taken off", async () => {
    const canteen: PayrollInputDto = {
      id: '0192a000-0000-7000-8000-00000000b1c1',
      employeeId: EMPLOYEE_ID,
      employeeCode: 'NHEMP00042',
      employeeName: 'Meena Raghavan',
      kind: 'Deduction',
      componentCode: 'CANTEEN',
      description: 'Canteen credit',
      amount: 750,
      source: 'Integration',
      sourceReference: 'canteen-bill-1',
    };
    const pending: PendingPayChange[] = [
      {
        approvalId: '0192a000-0000-7000-8000-00000000b1a3',
        kind: 'hr.payroll_input',
        employeeId: EMPLOYEE_ID,
        inputId: '0192a000-0000-7000-8000-00000000b1c2',
        summary: 'Earning INCENTIVE ₹3,000.00 for Meena Raghavan (NHEMP00042), payroll 07/2026',
        requestedBy: 'Hospital Administrator',
        requestedAt: '2026-11-08T05:00:00Z',
      },
    ];
    const server = fakeServer({
      [`GET /api/v1/hr/payroll/runs/${RUN_ID}/inputs`]: [canteen],
      'GET /api/v1/hr/payroll/pending-changes': pending,
      'GET /api/v1/hr/payroll/components': [] as SalaryComponentDto[],
      [`DELETE /api/v1/hr/payroll/inputs/${canteen.id}`]: asked(
        'hr.payroll_input_remove',
        'Deduction CANTEEN ₹750.00 taken off Meena Raghavan (NHEMP00042), payroll 07/2026',
      ),
    });
    const user = userEvent.setup();
    show(<PayrollInputsPanel runId={RUN_ID} editable />);

    expect(await screen.findByText('Asked for approval: 1 change(s) wait', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/Earning INCENTIVE ₹3,000.00 for Meena Raghavan/)).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Remove this input' }));
    const confirm = await screen.findByRole('alertdialog');
    expect(within(confirm).getByText('Ask for this deduction to be removed?')).toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(within(confirm).getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining('deduction stays')),
    );
    expect(server.requests.some((r) => r.method === 'DELETE')).toBe(true);
  });

  it("explains that nobody credits their own leave, and asks for another's credit", async () => {
    let own = true;
    const server = fakeServer({
      'GET /api/v1/hr/leave/types': [
        { id: LEAVE_TYPE_ID, code: 'CL', name: 'Casual Leave', tracksBalance: true, isActive: true },
      ],
      'POST /api/v1/hr/leave/balances/adjust': () =>
        own
          ? refused(403, 'hr.leave_own', 'This is your own leave balance.')
          : asked('hr.leave_adjust', 'CL 2026 balance of Meena Raghavan (NHEMP00042) credited +2 day(s)'),
    });
    const user = userEvent.setup();
    show(
      <AdjustBalanceDialog
        open
        onClose={() => undefined}
        employee={{ id: EMPLOYEE_ID, code: 'NHEMP00042', name: 'Meena Raghavan' }}
        year={2026}
      />,
    );

    const dialog = await screen.findByRole('dialog', {}, SLOW);
    await user.selectOptions(await within(dialog).findByRole('combobox', { name: /Leave type/ }), LEAVE_TYPE_ID);
    const days = within(dialog).getByRole('spinbutton', { name: /^Days/ });
    await user.clear(days);
    await user.type(days, '2');
    await user.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Worked on Diwali');
    expect(
      within(dialog).getByText(/A credit waits for someone else who approves leave, never you/),
    ).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    expect(await within(dialog).findByText('Not adjusted')).toBeInTheDocument();
    expect(
      within(dialog).getByText(/This is your own leave: someone else in HR adjusts the balance/),
    ).toBeInTheDocument();
    await expectNoAxeViolations();

    own = false;
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));
    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining('approves the credit')),
    );
    expect(server.requests.filter((r) => r.method === 'POST')).toHaveLength(2);
  });
});
