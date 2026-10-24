import type * as Core from '@hc360/core';
import { todayIso, type PagedResult } from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  fakeServer,
  openScreen,
  paged,
  SCREEN_TESTS,
  SLOW,
  STAFF_USER,
  stubBrowserApis,
  type FakeRequest,
} from '@/test/appScreen';
import { problem } from '@/test/fakeApi';
import type { AccrualDto, DoctorStatement, PayoutDto, ShareRuleDto } from './api';
import { previousMonth } from './logic';

// The doctor-fee screens (DFA-01..05), whole with the shell, against a fake server: the sharing rules, fee accruals,
// payouts and statements; what each list shows, the rules its forms apply, the version a save is made on, and how the
// server's refusals read.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const MEERA = { id: STAFF_USER.practitionerId!, code: 'D001', name: 'Dr. Meera Iyer' };
const ANIL = { id: '0192a000-0000-7000-8000-0000000000d2', code: 'D002', name: 'Dr. Anil Rao' };
const id = (suffix: string) => `0192a000-0000-7000-8000-00000000f${suffix}`;

const rule = (overrides: Partial<ShareRuleDto> & Pick<ShareRuleDto, 'id'>): ShareRuleDto => ({
  practitionerId: null,
  practitionerName: null,
  serviceItemId: null,
  serviceCode: null,
  category: null,
  payerType: null,
  method: 'Percentage',
  value: 80,
  effectiveFrom: '2026-04-01',
  effectiveTo: null,
  isActive: true,
  notes: null,
  version: 1,
  ...overrides,
});

const DEFAULT_RULE = rule({ id: id('001'), category: 'Consultation' });
const ANIL_RULE = rule({
  id: id('002'),
  practitionerId: ANIL.id,
  practitionerName: ANIL.name,
  payerType: 'GovernmentScheme',
  value: 40,
  notes: 'CGHS rate',
  version: 3,
});
const OLD_RULE = rule({
  id: id('003'),
  practitionerId: MEERA.id,
  practitionerName: MEERA.name,
  method: 'FixedPerUnit',
  value: 150,
  effectiveTo: '2026-03-31',
  isActive: false,
});

const payout = (overrides: Partial<PayoutDto> & Pick<PayoutDto, 'id' | 'payoutNumber' | 'status'>): PayoutDto => ({
  practitionerId: ANIL.id,
  practitionerName: ANIL.name,
  periodFrom: '2026-09-01',
  periodTo: '2026-09-30',
  grossAmount: 20_000,
  deductionAmount: 2_000,
  tdsPercent: 10,
  tdsAmount: 2_000,
  netAmount: 16_000,
  approvedBy: null,
  approvedAt: null,
  paidAt: null,
  paidReference: null,
  paymentMode: null,
  notes: null,
  accrualCount: 12,
  deductions: [{ id: id('101'), description: 'Advance paid on 5 Sep', amount: 2_000 }],
  version: 2,
  ...overrides,
});

const DRAFT = payout({ id: id('201'), payoutNumber: 'DP-26-0007', status: 'Draft' });
const APPROVED = payout({
  id: id('202'),
  payoutNumber: 'DP-26-0006',
  status: 'Approved',
  practitionerId: MEERA.id,
  practitionerName: MEERA.name,
  grossAmount: 50_000,
  deductionAmount: 0,
  tdsAmount: 5_000,
  netAmount: 45_000,
  deductions: [],
  approvedBy: 'accounts',
  approvedAt: '2026-10-02T06:00:00Z',
  version: 5,
});
const PAID = payout({
  id: id('203'),
  payoutNumber: 'DP-26-0005',
  status: 'Paid',
  periodFrom: '2026-08-01',
  periodTo: '2026-08-31',
  paidAt: '2026-09-05T06:00:00Z',
  paidReference: 'UTR0001',
  paymentMode: 'NetBanking',
  version: 7,
});
const PAYOUTS = [DRAFT, APPROVED, PAID];

const accrual = (overrides: Partial<AccrualDto> & Pick<AccrualDto, 'id'>): AccrualDto => ({
  practitionerId: ANIL.id,
  practitionerName: ANIL.name,
  billId: id('301'),
  billNumber: 'NHOB260000210',
  serviceCode: 'CONS-NEW',
  description: 'Consultation, new patient',
  category: 'Consultation',
  payerType: 'Self',
  serviceDate: '2026-10-20',
  accruedOn: '2026-10-20',
  basisAmount: 500,
  method: 'Percentage',
  rate: 80,
  shareAmount: 400,
  status: 'Accrued',
  payoutId: null,
  isReversal: false,
  ...overrides,
});

const ACCRUALS = [
  accrual({ id: id('401') }),
  accrual({
    id: id('402'),
    billNumber: 'NHOB260000188',
    description: 'ECG',
    category: 'Cardiology',
    basisAmount: 300,
    method: 'FixedPerUnit',
    rate: 300,
    shareAmount: -300,
    status: 'Reversed',
    isReversal: true,
  }),
];

const STATEMENT: DoctorStatement = {
  practitionerId: ANIL.id,
  practitionerName: ANIL.name,
  from: '2026-10-01',
  to: todayIso(),
  accrued: 12_400,
  reversed: 300,
  inPayout: 0,
  paidOut: 0,
  unpaid: 12_100,
  accruals: ACCRUALS,
  payouts: [],
};

/** The server's answer to a save on a version that is no longer the record's. */
const concurrency = () =>
  new Response(JSON.stringify({ title: 'Changed meanwhile', status: 409, code: 'concurrency' }), {
    status: 409,
    headers: { 'content-type': 'application/problem+json' },
  });

let server: ReturnType<typeof fakeServer>;
/** The sharing rules as the server has them now. */
let rules: ShareRuleDto[];

/** The payouts list, filtered by status the way the server does (the overview's counts ask with pageSize 1). */
function listPayouts(request: FakeRequest): PagedResult<PayoutDto> {
  const status = request.url.searchParams.get('status');
  return paged(PAYOUTS.filter((p) => !status || p.status === status));
}

/** A sharing rule saved on the version it was read at; the server moves the version on. */
function saveRule(request: FakeRequest) {
  const ruleId = request.path.split('/').at(-1);
  const body = request.body as { version: number; value: number };
  const current = rules.find((r) => r.id === ruleId);
  if (!current || current.version !== body.version) {
    return concurrency();
  }
  rules = rules.map((r) => (r.id === ruleId ? { ...r, value: body.value, version: r.version + 1 } : r));
  return null;
}

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  rules = [DEFAULT_RULE, ANIL_RULE];
  server = fakeServer({
    'GET /api/v1/masters/lookups/practitioners': [MEERA, ANIL],
    'GET /api/v1/print/letterhead': { name: 'Nana Hospitals, Pune' },
    'GET /api/v1/doctor-fees/rules': (request: FakeRequest) =>
      request.url.searchParams.get('includeInactive') === 'true' ? [...rules, OLD_RULE] : rules,
    'POST /api/v1/doctor-fees/rules': { id: id('004') },
    'PUT /api/v1/doctor-fees/rules/*': saveRule,
    'GET /api/v1/doctor-fees/accruals': paged(ACCRUALS),
    'GET /api/v1/doctor-fees/payouts': listPayouts,
    'GET /api/v1/doctor-fees/payouts/*': (request: FakeRequest) =>
      PAYOUTS.find((p) => p.id === request.path.split('/').at(-1)),
    'POST /api/v1/doctor-fees/payouts': { id: DRAFT.id },
    'POST /api/v1/doctor-fees/payouts/*': null,
    'GET /api/v1/doctor-fees/statements': STATEMENT,
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** What was sent to `method path`, in order. */
const sent = (method: string, path: string) =>
  server.requests.filter((request) => request.method === method && request.path === path).map((r) => r.body);

/** The open drawer or dialog, by its title. */
const dialogTitled = async (title: string | RegExp) =>
  (await screen.findByRole('dialog', { name: title }, SLOW)) as HTMLElement;

describe('doctor fee screens (DFA-01..05)', SCREEN_TESTS, () => {
  it('the overview counts the payouts by status and lists the recent ones', async () => {
    openScreen('/doctor-fees');
    expect(await screen.findByRole('table', { name: 'Recent payouts' }, SLOW)).toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'Recent payouts' });
    expect((await within(table).findAllByText('₹16,000.00', {}, SLOW)).length).toBeGreaterThan(0);
    expect(await within(table).findByText('Approved, to pay')).toBeInTheDocument();
    // One payout in each status, counted with the status filter the server applies.
    await waitFor(() =>
      expect(
        server.requests
          .filter((r) => r.url.searchParams.get('pageSize') === '1')
          .map((r) => r.url.searchParams.get('status'))
          .sort(),
      ).toEqual(['Approved', 'Draft', 'Paid']),
    );
  });

  it('lists the sharing rules: the hospital default and a doctor’s own, and the inactive ones on request', async () => {
    const user = userEvent.setup();
    openScreen('/doctor-fees/rules');
    const table = await screen.findByRole('table', { name: 'Sharing rules' }, SLOW);
    expect(await within(table).findByText('All doctors (default)', {}, SLOW)).toBeInTheDocument();
    expect(await within(table).findByText('Consultation')).toBeInTheDocument();
    expect(await within(table).findByText('80%')).toBeInTheDocument();
    expect(await within(table).findByText('Dr. Anil Rao')).toBeInTheDocument();
    expect(await within(table).findByText('Every service · Government scheme patients')).toBeInTheDocument();
    expect(await within(table).findByText('40%')).toBeInTheDocument();
    expect(within(table).queryByText('₹150.00 per unit')).not.toBeInTheDocument();

    await user.click(screen.getByRole('switch', { name: 'Show inactive' }));
    expect(await within(table).findByText('₹150.00 per unit', {}, SLOW)).toBeInTheDocument();
    expect(await within(table).findByText('Inactive')).toBeInTheDocument();
  });

  it('a sharing rule is saved on the version it was opened at', async () => {
    const user = userEvent.setup();
    openScreen('/doctor-fees/rules');
    await user.click(await screen.findByText('Dr. Anil Rao', {}, SLOW));
    const drawer = await dialogTitled('Sharing rule');
    const percent = within(drawer).getByRole('textbox', { name: /^Percent/ });
    expect(percent).toHaveValue('40');
    await user.clear(percent);
    await user.type(percent, '45');
    await user.click(within(drawer).getByRole('button', { name: 'Save rule' }));

    await waitFor(() => expect(sent('PUT', `/api/v1/doctor-fees/rules/${ANIL_RULE.id}`)).toHaveLength(1));
    expect(sent('PUT', `/api/v1/doctor-fees/rules/${ANIL_RULE.id}`)[0]).toEqual({
      practitionerId: ANIL.id,
      serviceItemId: null,
      category: null,
      payerType: 'GovernmentScheme',
      method: 'Percentage',
      value: 45,
      effectiveFrom: '2026-04-01',
      effectiveTo: null,
      isActive: true,
      notes: 'CGHS rate',
      version: 3,
    });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Sharing rule' })).not.toBeInTheDocument());
  });

  it('a rule someone else saved meanwhile is not overwritten: the refusal is shown and the drawer stays open', async () => {
    const user = userEvent.setup();
    openScreen('/doctor-fees/rules');
    await user.click(await screen.findByText('Dr. Anil Rao', {}, SLOW));
    const drawer = await dialogTitled('Sharing rule');
    // Accounts changes the same rule from another desk while this one is open.
    rules = rules.map((r) => (r.id === ANIL_RULE.id ? { ...r, value: 42, version: 4 } : r));
    const percent = within(drawer).getByRole('textbox', { name: /^Percent/ });
    await user.clear(percent);
    await user.type(percent, '45');
    await user.click(within(drawer).getByRole('button', { name: 'Save rule' }));

    expect(await within(drawer).findByText('Someone else changed this record', {}, SLOW)).toBeInTheDocument();
    expect((sent('PUT', `/api/v1/doctor-fees/rules/${ANIL_RULE.id}`)[0] as { version: number }).version).toBe(3);
    expect(rules.find((r) => r.id === ANIL_RULE.id)?.value).toBe(42);
    expect(screen.getByRole('dialog', { name: 'Sharing rule' })).toBeInTheDocument();
  });

  it('a new rule needs a share of 0–100 % and, for a category, the category; only what the scope names is sent', async () => {
    const user = userEvent.setup();
    openScreen('/doctor-fees/rules');
    await screen.findByText('Dr. Anil Rao', {}, SLOW);
    await user.click(screen.getByRole('button', { name: 'Add rule' }));
    const drawer = await dialogTitled('Add a sharing rule');
    const add = within(drawer).getByRole('button', { name: 'Add rule' });
    await waitFor(() => expect(add).toBeDisabled());

    await user.click(within(drawer).getByRole('radio', { name: 'A category' }));
    const percent = within(drawer).getByRole('textbox', { name: /^Percent/ });
    await user.type(percent, '150');
    expect(await within(drawer).findByText('Enter a percentage between 0 and 100.')).toBeInTheDocument();
    await waitFor(() => expect(add).toBeDisabled());
    await user.clear(percent);
    await user.type(percent, '60');
    expect(within(drawer).queryByText('Enter a percentage between 0 and 100.')).not.toBeInTheDocument();
    // Still no category chosen.
    await waitFor(() => expect(add).toBeDisabled());

    await user.selectOptions(within(drawer).getByRole('combobox', { name: /^Category/ }), 'Laboratory');
    await waitFor(() => expect(add).toBeEnabled());
    await user.click(add);

    await waitFor(() => expect(sent('POST', '/api/v1/doctor-fees/rules')).toHaveLength(1));
    expect(sent('POST', '/api/v1/doctor-fees/rules')[0]).toEqual({
      practitionerId: null,
      serviceItemId: null,
      category: 'Laboratory',
      payerType: null,
      method: 'Percentage',
      value: 60,
      effectiveFrom: todayIso(),
      effectiveTo: null,
      isActive: true,
      notes: null,
      version: null,
    });
  });

  it('a list the server cannot load says so and offers to retry', async () => {
    server = fakeServer({
      'GET /api/v1/masters/lookups/practitioners': [MEERA, ANIL],
      'GET /api/v1/doctor-fees/rules': problem(500, 'The rules could not be read'),
    });
    openScreen('/doctor-fees/rules');
    expect((await screen.findAllByText('The rules could not be read', {}, SLOW)).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });

  it('lists the fees earned per bill line, with reversals, and their total on the page', async () => {
    openScreen('/doctor-fees/accruals');
    const table = await screen.findByRole('table', { name: 'Fee accruals' }, SLOW);
    expect(await within(table).findByText('Consultation, new patient', {}, SLOW)).toBeInTheDocument();
    expect(await within(table).findByText('Reversal')).toBeInTheDocument();
    expect(await within(table).findByText('80%')).toBeInTheDocument();
    expect(await within(table).findByText('₹300.00/unit')).toBeInTheDocument();
    expect(screen.getByText('2 fee lines · this page ₹100.00')).toBeInTheDocument();
  });

  it('lists the payouts; a draft is approved on the version read, and the server’s refusal is shown', async () => {
    const user = userEvent.setup();
    let refuse = true;
    server = fakeServer({
      'GET /api/v1/masters/lookups/practitioners': [MEERA, ANIL],
      'GET /api/v1/print/letterhead': { name: 'Nana Hospitals, Pune' },
      'GET /api/v1/doctor-fees/payouts': listPayouts,
      'GET /api/v1/doctor-fees/payouts/*': (request: FakeRequest) =>
        PAYOUTS.find((p) => p.id === request.path.split('/').at(-1)),
      [`POST /api/v1/doctor-fees/payouts/${DRAFT.id}/approve`]: () =>
        refuse ? problem(422, 'You prepared this payout') : null,
    });
    openScreen('/doctor-fees/payouts');
    const table = await screen.findByRole('table', { name: 'Doctor payouts' }, SLOW);
    expect(await within(table).findByText('DP-26-0007', {}, SLOW)).toBeInTheDocument();
    expect(await within(table).findByText('Awaiting approval')).toBeInTheDocument();
    expect(await within(table).findByText('Approved, to pay')).toBeInTheDocument();
    expect(within(table).getAllByText('Paid').length).toBeGreaterThan(0);

    await user.click(within(table).getByText('DP-26-0007'));
    const dialog = await dialogTitled('Payout DP-26-0007');
    expect(await within(dialog).findByText('Less: Advance paid on 5 Sep', {}, SLOW)).toBeInTheDocument();
    expect(
      await within(dialog).findByText('The person who prepared this payout cannot approve it.'),
    ).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Approve' }));

    const confirm = await screen.findByRole('alertdialog', { name: 'Approve the payout of ₹16,000.00?' }, SLOW);
    await user.click(within(confirm).getByRole('button', { name: 'Approve' }));
    expect((await within(confirm).findAllByText('You prepared this payout', {}, SLOW)).length).toBeGreaterThan(0);

    refuse = false;
    await user.click(within(confirm).getByRole('button', { name: 'Approve' }));
    await waitFor(() =>
      expect(screen.queryByRole('alertdialog', { name: 'Approve the payout of ₹16,000.00?' })).not.toBeInTheDocument(),
    );
    expect(sent('POST', `/api/v1/doctor-fees/payouts/${DRAFT.id}/approve`)).toEqual([{ version: 2 }, { version: 2 }]);
  });

  it('an approved payout is paid with a reference, unless in cash', async () => {
    const user = userEvent.setup();
    openScreen('/doctor-fees/payouts');
    await user.click(await screen.findByText('DP-26-0006', {}, SLOW));
    const dialog = await dialogTitled('Payout DP-26-0006');
    await user.click(await within(dialog).findByRole('button', { name: 'Record payment' }, SLOW));

    const confirm = within(dialog).getByRole('button', { name: /^Confirm paid/ });
    expect(confirm).toHaveTextContent('₹45,000.00');
    await waitFor(() => expect(confirm).toBeDisabled());
    const mode = within(dialog).getByRole('combobox', { name: /^Paid by/ });
    await user.selectOptions(mode, 'Cash');
    await waitFor(() => expect(confirm).toBeEnabled());
    await user.selectOptions(mode, 'NetBanking');
    await waitFor(() => expect(confirm).toBeDisabled());

    await user.type(within(dialog).getByRole('textbox', { name: /^Transfer or cheque reference/ }), 'UTR2610020001');
    await user.click(confirm);
    await waitFor(() => expect(sent('POST', `/api/v1/doctor-fees/payouts/${APPROVED.id}/pay`)).toHaveLength(1));
    expect(sent('POST', `/api/v1/doctor-fees/payouts/${APPROVED.id}/pay`)[0]).toEqual({
      mode: 'NetBanking',
      reference: 'UTR2610020001',
      version: 5,
    });
  });

  it('a payout is prepared for a doctor and last month, with TDS of 0–100 % and described deductions', async () => {
    const user = userEvent.setup();
    openScreen('/doctor-fees/payouts');
    await screen.findByText('DP-26-0007', {}, SLOW);
    await user.click(screen.getByRole('button', { name: 'Prepare payout' }));
    const dialog = await dialogTitled('Prepare a doctor payout');
    const prepare = within(dialog).getByRole('button', { name: 'Prepare payout' });
    await waitFor(() => expect(prepare).toBeDisabled());

    await user.type(within(dialog).getByRole('combobox', { name: /^Doctor/ }), 'Anil');
    await user.click(await screen.findByRole('option', { name: 'Dr. Anil Rao (D002)' }, SLOW));
    await waitFor(() => expect(prepare).toBeEnabled());

    const tds = within(dialog).getByRole('textbox', { name: /^TDS/ });
    await user.type(tds, '150');
    expect(await within(dialog).findByText('Enter a percentage between 0 and 100.')).toBeInTheDocument();
    await waitFor(() => expect(prepare).toBeDisabled());
    await user.clear(tds);
    await user.type(tds, '10');

    await user.click(within(dialog).getByRole('button', { name: 'Add a deduction' }));
    expect(await within(dialog).findByText('Every deduction needs a description and an amount.')).toBeInTheDocument();
    await waitFor(() => expect(prepare).toBeDisabled());
    await user.type(within(dialog).getByRole('textbox', { name: 'Description' }), 'Advance paid on 5 Sep');
    await user.type(within(dialog).getByRole('textbox', { name: /^Amount/ }), '2000');
    await waitFor(() => expect(prepare).toBeEnabled());
    await user.click(prepare);

    const period = previousMonth(todayIso());
    await waitFor(() => expect(sent('POST', '/api/v1/doctor-fees/payouts')).toHaveLength(1));
    expect(sent('POST', '/api/v1/doctor-fees/payouts')[0]).toEqual({
      practitionerId: ANIL.id,
      periodFrom: period.from,
      periodTo: period.to,
      deductions: [{ description: 'Advance paid on 5 Sep', amount: 2000 }],
      tdsPercent: 10,
      notes: null,
    });
    // The new payout opens, for its approval by someone else.
    expect(await dialogTitled('Payout DP-26-0007')).toBeInTheDocument();
  });

  it("a doctor's statement for the period, once the doctor is chosen", async () => {
    const user = userEvent.setup();
    openScreen('/doctor-fees/statements');
    expect(await screen.findByText('Choose a doctor', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Print statement' })).toBeDisabled();

    await user.click(screen.getByRole('combobox', { name: /^Doctor/ }));
    await user.click(await screen.findByRole('option', { name: 'Dr. Anil Rao (D002)' }, SLOW));
    expect(await screen.findByText('₹12,400.00', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('₹12,100.00')).toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'Fees by bill line' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Print statement' })).toBeEnabled();
    const asked = server.requests.find((r) => r.path === '/api/v1/doctor-fees/statements');
    expect(asked?.url.searchParams.get('practitionerId')).toBe(ANIL.id);
    expect(asked?.url.searchParams.get('from')).toBe(`${todayIso().slice(0, 8)}01`);
  });
});
