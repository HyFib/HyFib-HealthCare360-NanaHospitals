import type * as Core from '@hc360/core';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { AuditRetentionStatus, RetentionRuleDto } from './api';

// How long the audit trail is kept (ADR-015), on the retention screen: a super administrator asks to change the period
// (never fewer than 8 years) or to purge the months it no longer keeps, and another approves under Approvals. Someone
// who keeps the schedule without admin.system.manage sees neither, nor asks the server for them.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const AUDIT_RULE: RetentionRuleDto = {
  key: 'audit',
  title: 'Audit trail',
  description: 'Who opened, changed, printed or exported each record, and when.',
  basis: 'Kept permanently unless two super administrators set 8 years at the least.',
  action: 'Keep',
  unit: 'Years',
  value: 8,
  defaultValue: null,
  minimumValue: 8,
  maximumValue: 99,
  editable: false,
  systemManaged: true,
  lastRemoved: null,
  lastRunAt: null,
  version: 3,
};

const STATUS: AuditRetentionStatus = {
  years: 8,
  minimumYears: 8,
  maximumYears: 99,
  basis: 'Books of account and their audit trail are kept 8 years (Companies Act s.128(5)).',
  purge: {
    years: 8,
    cutoffMonth: '2018-11-01',
    months: [
      { month: '2015-03-01', rows: 2, firstChainIndex: 1, lastChainIndex: 2 },
      { month: '2015-04-01', rows: 1, firstChainIndex: 3, lastChainIndex: 3 },
    ],
    rows: 3,
    firstChainIndex: 1,
    boundaryChainIndex: 3,
    boundaryHash: 'A'.repeat(64),
    problem: null,
  },
  pendingChange: null,
  pendingPurge: null,
  lastPurge: null,
};

const ASKED = {
  id: '0192a000-0000-7000-8000-00000000e001',
  summary: 'Audit trail kept for 10 years (now for 8 years)',
};

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  server = fakeServer({
    'GET /api/v1/privacy/retention': [AUDIT_RULE],
    'GET /api/v1/privacy/retention/runs': [],
    'GET /api/v1/privacy/retention/review': { outpatientChartsDue: 0, inpatientChartsDue: 0, erasureHolds: [] },
    'GET /api/v1/admin/audit/retention': STATUS,
    'POST /api/v1/admin/audit/retention/change': ASKED,
    'POST /api/v1/admin/audit/retention/purge': { ...ASKED, summary: 'Purge of the audit trail before November 2018' },
  });
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the audit trail on the retention screen', SCREEN_TESTS, () => {
  it('asks another super administrator to change the period, never to fewer than 8 years', async () => {
    const user = userEvent.setup();
    openScreen('/privacy/retention');
    // The schedule's own row says who sets it.
    expect(
      (
        await screen.findAllByText(
          'Set by super administrators, two people agreeing, under Audit trail below.',
          {},
          SLOW,
        )
      ).length,
    ).toBeGreaterThan(0);
    await user.click(await screen.findByRole('button', { name: 'Ask to change' }, SLOW));

    const dialog = await screen.findByRole('dialog', { name: 'How long to keep the audit trail' });
    const years = within(dialog).getByRole('spinbutton', { name: /Years/ });
    await user.clear(years);
    await user.type(years, '7');
    expect(
      within(dialog).getByText(/At least 8 years: the audit trail also records changes to bills and payments/),
    ).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Ask for approval' })).toBeDisabled();

    await user.clear(years);
    await user.type(years, '10');
    expect(within(dialog).getByText(/^Months before .* may then be purged/)).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'The auditors asked for ten years.');
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    expect(server.requests.find((r) => r.method === 'POST')?.body).toEqual({
      years: 10,
      reason: 'The auditors asked for ten years.',
    });
  });

  it('asks for permanently again', async () => {
    const user = userEvent.setup();
    openScreen('/privacy/retention');
    await user.click(await screen.findByRole('button', { name: 'Ask to change' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: 'How long to keep the audit trail' });
    await user.click(within(dialog).getByRole('radio', { name: 'Permanently' }));
    expect(
      within(dialog).getByText('Nothing is ever purged: the audit trail is kept permanently.'),
    ).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'Keep everything.');
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    expect(server.requests.find((r) => r.method === 'POST')?.body).toEqual({ years: null, reason: 'Keep everything.' });
  });

  it('shows what a purge would remove before asking for it', async () => {
    const user = userEvent.setup();
    openScreen('/privacy/retention');
    await user.click(await screen.findByRole('button', { name: 'Ask to purge' }, SLOW));

    const dialog = await screen.findByRole('dialog', { name: 'Purge the audit trail before November 2018?' });
    expect(within(dialog).getByText('March 2015')).toBeInTheDocument();
    expect(within(dialog).getByText('April 2015')).toBeInTheDocument();
    expect(within(dialog).getByText(/^3 records in all/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Ask for approval' })).toBeDisabled();
    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'Older than the books of account.');
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    expect(server.requests.find((r) => r.method === 'POST' && r.path.endsWith('/purge'))?.body).toEqual({
      reason: 'Older than the books of account.',
    });
  });

  it('is not shown to someone who keeps the schedule without admin.system.manage', async () => {
    openScreen('/privacy/retention', {
      ...STAFF_USER,
      roles: ['HospitalAdmin'],
      permissions: ['privacy.retention.manage'],
    });
    expect(await screen.findByText('Audit trail', {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ask to change' })).not.toBeInTheDocument();
    expect(server.requests.some((r) => r.path === '/api/v1/admin/audit/retention')).toBe(false);
  });
});
