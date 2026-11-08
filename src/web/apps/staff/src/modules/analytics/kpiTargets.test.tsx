import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { KpiTargetItem, KpiTargets } from './api';

// The KPI targets screen, whole with the shell, against a fake server: the facility's KPIs with their targets, setting
// one (the screen names a threshold on the wrong side before sending anything, then sends the target without a version
// the first time) and changing one with the version it loaded.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const TARGETS = '/api/v1/analytics/kpi-targets';

const visits: KpiTargetItem = {
  key: 'opVisits',
  label: 'OP visits',
  unit: 'count',
  higherIsBetter: true,
  monthly: true,
  formula: 'Check-ins, excluding cancelled visits.',
  target: null,
};

const occupancy: KpiTargetItem = {
  key: 'occupancy',
  label: 'Bed occupancy',
  unit: 'percent',
  higherIsBetter: true,
  monthly: false,
  formula: 'Inpatient days ÷ available bed-days × 100.',
  target: {
    target: 85,
    amberThreshold: 80,
    redThreshold: 70,
    ownerRole: 'HospitalAdmin',
    setByName: 'Dr. Meera Iyer',
    setAt: '2026-11-01T05:00:00Z',
    lastStatus: 'Amber',
    lastValue: 76.4,
    lastCheckedAt: '2026-11-08T04:30:00Z',
    version: 3,
  },
};

const LIST: KpiTargets = { facilityName: 'Nana Hospitals, Pune', roles: ['Accountant', 'HospitalAdmin'], kpis: [visits, occupancy] };

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('KPI targets', SCREEN_TESTS, () => {
  it('lists each KPI with its target and this month’s status, and sets a first target after naming a wrong threshold', async () => {
    server = fakeServer({
      [`GET ${TARGETS}`]: LIST,
      [`PUT ${TARGETS}/opVisits`]: { ...visits, target: { ...occupancy.target!, target: 3000, amberThreshold: 2500, redThreshold: 2000, version: 1 } },
    });
    const user = userEvent.setup();
    openScreen('/analytics/kpi-targets');

    const table = await screen.findByRole('table', { name: 'KPI targets' }, SLOW);
    expect(within(table).getByText('Needs attention')).toBeInTheDocument();
    expect(within(table).getByText('A monthly figure, prorated to the period shown; higher is better.')).toBeInTheDocument();
    expect(screen.getByText(/1 of 2 KPIs have a target/)).toBeInTheDocument();
    await expectNoAxeViolations();

    await user.click(within(table).getByRole('button', { name: 'Set a target for OP visits' }));
    const dialog = await screen.findByRole('dialog', { name: 'Target for OP visits' });
    expect(within(dialog).getByRole('combobox', { name: /Tell this role/ })).toHaveValue('HospitalAdmin');
    await user.type(within(dialog).getByRole('textbox', { name: /^Target/ }), '3000');
    await user.type(within(dialog).getByRole('textbox', { name: /^Amber at/ }), '3500');
    await user.type(within(dialog).getByRole('textbox', { name: /^Red at/ }), '2000');
    await user.click(within(dialog).getByRole('button', { name: 'Save target' }));

    expect(
      await within(dialog).findByText('Higher is better for this KPI, so the amber threshold must be at or below the target.'),
    ).toBeInTheDocument();
    expect(server.requests.filter((r) => r.method === 'PUT')).toEqual([]);
    await expectNoAxeViolations();

    const amber = within(dialog).getByRole('textbox', { name: /^Amber at/ });
    await user.clear(amber);
    await user.type(amber, '2500');
    await user.click(within(dialog).getByRole('button', { name: 'Save target' }));

    expect(await screen.findByText('Target for OP visits saved', {}, SLOW)).toBeInTheDocument();
    const puts = server.requests.filter((r) => r.method === 'PUT');
    expect(puts).toHaveLength(1);
    expect(puts[0]?.body).toEqual({ target: 3000, amberThreshold: 2500, redThreshold: 2000, ownerRole: 'HospitalAdmin' });
  });

  it('changes a target with the version it loaded', async () => {
    server = fakeServer({
      [`GET ${TARGETS}`]: LIST,
      [`PUT ${TARGETS}/occupancy`]: occupancy,
    });
    const user = userEvent.setup();
    openScreen('/analytics/kpi-targets');

    const table = await screen.findByRole('table', { name: 'KPI targets' }, SLOW);
    await user.click(within(table).getByRole('button', { name: 'Change the target for Bed occupancy' }));
    const dialog = await screen.findByRole('dialog', { name: 'Target for Bed occupancy' });
    const target = within(dialog).getByRole('textbox', { name: /^Target/ });
    expect(target).toHaveValue('85');
    await user.clear(target);
    await user.type(target, '88');
    await user.selectOptions(within(dialog).getByRole('combobox', { name: /Tell this role/ }), 'Accountant');
    await user.click(within(dialog).getByRole('button', { name: 'Save target' }));

    expect(await screen.findByText('Target for Bed occupancy saved', {}, SLOW)).toBeInTheDocument();
    expect(server.requests.find((r) => r.method === 'PUT')?.body).toEqual({
      target: 88,
      amberThreshold: 80,
      redThreshold: 70,
      ownerRole: 'Accountant',
      version: 3,
    });
  });
});
