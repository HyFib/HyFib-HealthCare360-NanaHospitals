import { useSession } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { DoseLimitDto, DrugInteractionDto } from './prescriptionsApi';
import { DoseLimitDrawer, InteractionDrawer } from './SafetyTableEditors';

// The drug-safety tables (slice 5): tightening is saved at once; loosening an interaction or a maximum dose says it waits
// for the pharmacy and therapeutics committee, asks why, sends the reason with the change, and says it was asked for.

const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const MAJOR: DrugInteractionDto = {
  id: '0192a000-0000-7000-8000-00000000f101',
  drugA: 'aspirin',
  drugB: 'warfarin',
  severity: 'Major',
  effect: 'Additive bleeding risk',
  management: 'Avoid; if essential monitor INR closely',
  isActive: true,
  version: 3,
  approvalWaitingSince: null,
};

const LIMIT: DoseLimitDto = {
  id: '0192a000-0000-7000-8000-00000000f201',
  genericName: 'paracetamol',
  maxDailyDoseMg: 4000,
  route: 'oral',
  notes: null,
  isActive: true,
  version: 2,
  approvalWaitingSince: null,
};

const ASKED = {
  id: '0192a000-0000-7000-8000-00000000f301',
  kind: 'clinical.drug_interaction',
  summary: 'aspirin + warfarin',
};

let server: ReturnType<typeof fakeServer>;
const puts = () => server.requests.filter((r) => r.method === 'PUT');

function open(drawer: ReactNode) {
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 8 * 3600_000,
    user: STAFF_USER,
    locked: false,
  });
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Hc360ThemeProvider>{drawer}</Hc360ThemeProvider>
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

describe('loosening the drug-safety tables', SCREEN_TESTS, () => {
  it('asks the committee to approve an interaction made less severe, with the reason', async () => {
    server = fakeServer({ [`PUT /api/v1/clinical/drug-interactions/${MAJOR.id}`]: ASKED });
    const user = userEvent.setup();
    open(<InteractionDrawer interaction={MAJOR} onClose={vi.fn()} />);

    const drawer = await screen.findByRole('dialog', {}, SLOW);
    expect(within(drawer).getByRole('button', { name: 'Save' })).toBeInTheDocument();
    await user.click(within(drawer).getByRole('radio', { name: 'Moderate (warning)' }));
    expect(
      within(drawer).getByText(/waits for approval by the pharmacy and therapeutics committee/),
    ).toBeInTheDocument();
    await expectNoAxeViolations();

    await user.click(within(drawer).getByRole('button', { name: 'Ask for approval' }));
    expect(await within(drawer).findByText('Say why: it waits for approval.')).toBeInTheDocument();
    expect(puts()).toHaveLength(0);
    await user.type(within(drawer).getByRole('textbox', { name: /Why/ }), 'Regraded moderate in the March review');
    await user.click(within(drawer).getByRole('button', { name: 'Ask for approval' }));

    await vi.waitFor(() => expect(puts()).toHaveLength(1));
    expect(puts()[0]?.body).toMatchObject({
      severity: 'Moderate',
      reason: 'Regraded moderate in the March review',
      version: 3,
    });
    await vi.waitFor(() => expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.any(String)));
  });

  it('saves a maximum dose lowered at once, asking no reason', async () => {
    server = fakeServer({
      [`PUT /api/v1/clinical/dose-limits/${LIMIT.id}`]: { ...LIMIT, maxDailyDoseMg: 3000, version: 3 },
    });
    const user = userEvent.setup();
    open(<DoseLimitDrawer limit={LIMIT} onClose={vi.fn()} />);

    const drawer = await screen.findByRole('dialog', {}, SLOW);
    const max = within(drawer).getByRole('spinbutton', { name: /Maximum per day/ });
    await user.clear(max);
    await user.type(max, '3000');
    expect(within(drawer).queryByRole('textbox', { name: /Why/ })).not.toBeInTheDocument();
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));

    await vi.waitFor(() => expect(puts()).toHaveLength(1));
    expect(puts()[0]?.body).toMatchObject({ maxDailyDoseMg: 3000, reason: null });
    await vi.waitFor(() => expect(notify.success).toHaveBeenCalledWith('Dose limit saved'));
  });

  it('says raising a maximum dose waits for approval', async () => {
    server = fakeServer({});
    const user = userEvent.setup();
    open(<DoseLimitDrawer limit={LIMIT} onClose={vi.fn()} />);

    const drawer = await screen.findByRole('dialog', {}, SLOW);
    const max = within(drawer).getByRole('spinbutton', { name: /Maximum per day/ });
    await user.clear(max);
    await user.type(max, '6000');
    expect(within(drawer).getByText(/lets higher doses be signed without the block/)).toBeInTheDocument();
    expect(within(drawer).getByRole('button', { name: 'Ask for approval' })).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});
