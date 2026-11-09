import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '@/lib/api';
import { OrderRestraintDialog, RecordCheckDialog, Restraints, type RestraintEpisode } from './Restraints';

// A restraint on the nursing chart: the doctor's order, the nurse's check, and who sees which action. The server is
// replaced.

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), post: vi.fn() } }));

beforeAll(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

beforeEach(() => vi.clearAllMocks());

function wrap(children: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </FluentProvider>
  );
}

const ago = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

const ACTIVE: RestraintEpisode = {
  id: 'r1',
  admissionId: 'a1',
  type: 'Physical',
  method: 'Soft wrist holders, both wrists',
  reason: 'Pulling at the central line and striking staff',
  alternativesTried: 'Talking down, a sitter for an hour',
  checkIntervalMinutes: 15,
  orderedAt: ago(40),
  orderedByName: 'Dr. Demo Doctor',
  orderedUntil: ago(-80),
  renewals: [],
  status: 'Active',
  lastCheckedAt: ago(25),
  nextCheckDue: ago(10),
  checkOverdue: true,
  orderLapsed: false,
  releasedAt: null,
  releasedByName: null,
  releaseReason: null,
  debrief: null,
  debriefedAt: null,
  debriefedByName: null,
  checks: [
    {
      id: 'c1',
      checkedAt: ago(25),
      circulationOk: true,
      skinIntact: true,
      fluidsOffered: true,
      toiletingOffered: false,
      notes: 'Calmer',
      checkedByName: 'Nurse Demo',
      hasConcern: false,
    },
  ],
};

describe('restraint on the nursing chart', () => {
  it('shows the nurse the overdue check and lets them record it, but not renew or release', async () => {
    vi.mocked(api.get).mockResolvedValue([ACTIVE]);
    render(wrap(<Restraints admissionId="a1" canChart canOrder={false} />));

    expect(await screen.findByText('Check overdue')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Record a check' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Renew the order' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Release the patient' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Order a restraint' })).not.toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('lets the doctor renew or release, and order none while one is active', async () => {
    vi.mocked(api.get).mockResolvedValue([ACTIVE]);
    render(wrap(<Restraints admissionId="a1" canChart canOrder />));

    expect(await screen.findByRole('button', { name: 'Renew the order' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Release the patient' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Order a restraint' })).not.toBeInTheDocument();
  });

  it('orders a time-limited restraint with the alternatives tried', async () => {
    const user = userEvent.setup();
    vi.mocked(api.post).mockResolvedValue(ACTIVE);
    const onClose = vi.fn();
    render(wrap(<OrderRestraintDialog admissionId="a1" onClose={onClose} />));

    const order = screen.getByRole('button', { name: 'Order' });
    await user.type(screen.getByRole('textbox', { name: /What is used/ }), 'Soft wrist holders');
    await user.type(screen.getByRole('textbox', { name: /Why/ }), 'Pulling at the central line');
    expect(order).toBeDisabled();
    await user.type(screen.getByRole('textbox', { name: /tried first/ }), 'Talking down, a sitter');
    await user.selectOptions(screen.getByRole('combobox', { name: /how many hours/ }), '1');
    await expectNoAxeViolations();
    await user.click(order);

    expect(api.post).toHaveBeenCalledWith('/api/v1/nursing/admissions/a1/restraints', {
      type: 'Physical',
      method: 'Soft wrist holders',
      reason: 'Pulling at the central line',
      alternativesTried: 'Talking down, a sitter',
      hours: 1,
      checkIntervalMinutes: 15,
    });
    expect(onClose).toHaveBeenCalled();
  });

  it('asks what was done when the check finds poor circulation', async () => {
    const user = userEvent.setup();
    vi.mocked(api.post).mockResolvedValue(ACTIVE);
    render(wrap(<RecordCheckDialog admissionId="a1" episode={ACTIVE} onClose={vi.fn()} />));

    const record = screen.getByRole('button', { name: 'Record' });
    expect(record).toBeEnabled();
    await user.click(screen.getByRole('switch', { name: /Circulation good/ }));
    expect(record).toBeDisabled();
    await user.type(
      screen.getByRole('textbox', { name: /what was done/ }),
      'Left hand cool: holder loosened, doctor told',
    );
    await user.click(record);

    expect(api.post).toHaveBeenCalledWith(
      '/api/v1/nursing/restraints/r1/checks',
      expect.objectContaining({
        circulationOk: false,
        skinIntact: true,
        notes: 'Left hand cool: holder loosened, doctor told',
      }),
    );
  });
});
