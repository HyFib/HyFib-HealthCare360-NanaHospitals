import type * as Core from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { ASHA, BILLING_ANSWERS } from '@/test/accessibility/fixtures/billing';
import { problem } from '@/test/fakeApi';

// The OP billing counter, whole, with canned data: cancelling an unbilled service from the patient's visit card
// (billing.charges.manage), and adding one by hand to a visit with no draft bill.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const CASHIER = { ...STAFF_USER, roles: ['Billing'], permissions: ['billing.op.create'] };

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const chargeReads = (requests: { method: string; path: string }[]) =>
  requests.filter((r) => r.method === 'GET' && r.path === '/api/v1/billing/charges').length;

describe('OP billing: unbilled charges', SCREEN_TESTS, () => {
  it('cancels an unbilled service with a reason, then reads the services again', async () => {
    const server = fakeServer({ ...BILLING_ANSWERS, 'POST /api/v1/billing/charges/c2/cancel': null });
    const user = userEvent.setup();
    openScreen(`/billing/op?patientId=${ASHA.id}`);

    await user.click(await screen.findByRole('button', { name: 'Cancel Complete blood count' }, SLOW));
    const dialog = await screen.findByRole('alertdialog', { name: 'Cancel Complete blood count?' }, SLOW);
    // A lab charge: the order itself is not cancelled here.
    expect(dialog).toHaveTextContent('If it came from an order or a dispense, that is not cancelled here');
    const before = chargeReads(server.requests);
    await user.type(within(dialog).getByRole('textbox'), 'Sample not collected');
    await user.click(within(dialog).getByRole('button', { name: 'Cancel charge' }));

    await waitFor(() =>
      expect(server.requests.find((r) => r.path === '/api/v1/billing/charges/c2/cancel')?.body).toEqual({
        reason: 'Sample not collected',
      }),
    );
    await waitFor(() => expect(chargeReads(server.requests)).toBeGreaterThan(before));
    expect(await screen.findByText('Charge cancelled', {}, SLOW)).toBeInTheDocument();
  });

  it('shows the server problem when the service cannot be cancelled', async () => {
    fakeServer({
      ...BILLING_ANSWERS,
      'POST /api/v1/billing/charges/c2/cancel': problem(
        422,
        'This service is on a finalized bill. Cancel the bill or issue a credit note instead.',
      ),
    });
    const user = userEvent.setup();
    openScreen(`/billing/op?patientId=${ASHA.id}`);

    await user.click(await screen.findByRole('button', { name: 'Cancel Complete blood count' }, SLOW));
    const dialog = await screen.findByRole('alertdialog', { name: 'Cancel Complete blood count?' }, SLOW);
    await user.type(within(dialog).getByRole('textbox'), 'Charged twice');
    await user.click(within(dialog).getByRole('button', { name: 'Cancel charge' }));

    // The toast: the server's title, and its message (the same words for a rule without details).
    expect(
      (
        await screen.findAllByText(
          'This service is on a finalized bill. Cancel the bill or issue a credit note instead.',
          {},
          SLOW,
        )
      ).length,
    ).toBeGreaterThan(0);
    expect(screen.queryByText('Charge cancelled')).not.toBeInTheDocument();
    expect(screen.getByRole('alertdialog', { name: 'Cancel Complete blood count?' })).toBeInTheDocument();
  });

  it('offers adding a charge to a visit without a draft bill', async () => {
    fakeServer(BILLING_ANSWERS);
    const user = userEvent.setup();
    openScreen(`/billing/op?patientId=${ASHA.id}`);

    // Today's visit has a draft bill (services are added there); the earlier visit takes a charge here.
    await user.click((await screen.findAllByRole('button', { name: 'Add charge' }, SLOW))[0]!);
    const dialog = await screen.findByRole('alertdialog', { name: 'Add a charge' }, SLOW);
    expect(dialog).toHaveTextContent(/^Add a charge.*Visit /);
    expect(within(dialog).getByRole('button', { name: 'Add charge' })).toBeDisabled();
  });

  it('offers neither to a cashier without billing.charges.manage', async () => {
    fakeServer(BILLING_ANSWERS);
    openScreen(`/billing/op?patientId=${ASHA.id}`, CASHIER);

    expect(await screen.findByText('Complete blood count', {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel Complete blood count' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add charge' })).not.toBeInTheDocument();
  });
});
