import { useSession } from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { AlertIncidents } from './AlertIncidents';
import { alertStatus, minutesText, reportSummary, type AlertIncident, type AlertReport } from './alertIncidentsApi';

// Critical alerts on System health (audit P2): where each stands, the month's figures, and acknowledging an open one.

const ALERTS = '/api/v1/admin/system/alerts';

const alert = (overrides: Partial<AlertIncident> = {}): AlertIncident => ({
  id: '0194f2a0-0000-7000-8000-00000000e101',
  code: 'backup.stale',
  server: null,
  message: 'No successful backup in the last 36 hours.',
  level: 'OnCall',
  raisedAt: '2026-11-02T02:00:00Z',
  toldAt: '2026-11-02T02:00:00Z',
  firstDeliveredAt: null,
  firstReadAt: null,
  secondContactAt: null,
  supportAt: null,
  acknowledgedAt: null,
  acknowledgedBy: null,
  clearedAt: null,
  minutesToAcknowledge: null,
  messages: [],
  ...overrides,
});

const report = (incidents: AlertIncident[], overrides: Partial<AlertReport> = {}): AlertReport => ({
  from: '2026-10-03T02:00:00Z',
  to: '2026-11-02T02:30:00Z',
  total: incidents.length,
  acknowledged: 0,
  unacknowledged: incidents.length,
  reachedSecondContact: 0,
  reachedSupport: 0,
  medianMinutesToAcknowledge: null,
  ninetiethMinutesToAcknowledge: null,
  incidents,
  ...overrides,
});

describe('alertStatus and reportSummary', () => {
  it('says how far an alert nobody acknowledged has gone, and who acknowledged one and how soon', () => {
    expect(alertStatus(alert())).toEqual({ text: 'Not acknowledged: it has reached IT on-call', tone: 'danger' });
    expect(alertStatus(alert({ level: 'Support' })).text).toBe('Not acknowledged: it has reached HyFib support');
    expect(alertStatus(alert({ toldAt: null })).tone).toBe('warning');
    expect(alertStatus(alert({ acknowledgedAt: '2026-11-02T02:04:00Z', acknowledgedBy: 'IT Officer' }))).toEqual({
      text: 'Acknowledged by IT Officer 4 min after it was told; still there',
      tone: 'informative',
    });
    expect(alertStatus(alert({ acknowledgedAt: '2026-11-02T02:04:00Z', clearedAt: '2026-11-02T03:00:00Z' })).tone).toBe(
      'success',
    );
    expect(alertStatus(alert({ level: 'SecondContact', clearedAt: '2026-11-02T03:00:00Z' }))).toEqual({
      text: 'Cleared before anyone acknowledged it (it reached the second contact)',
      tone: 'warning',
    });
  });

  it('puts the month in figures', () => {
    expect(reportSummary(report([]))).toBe('No critical alert in the last 30 days.');
    expect(
      reportSummary(
        report([alert(), alert(), alert()], {
          acknowledged: 2,
          unacknowledged: 1,
          reachedSecondContact: 1,
          reachedSupport: 1,
          medianMinutesToAcknowledge: 0.4,
          ninetiethMinutesToAcknowledge: 75,
        }),
      ),
    ).toBe(
      '3 critical alerts in the last 30 days; acknowledged in under a minute at the median and 1 h 15 min at the 90th percentile; 1 not acknowledged; 1 reached the second contact; 1 reached HyFib support.',
    );
    expect(minutesText(12.4)).toBe('12 min');
    expect(minutesText(120)).toBe('2 h');
  });
});

describe('the alerts on System health', () => {
  let server: ReturnType<typeof fakeServer>;

  beforeEach(() => {
    stubBrowserApis();
    useSession.setState({
      status: 'authenticated',
      accessToken: 'test-token',
      expiresAt: Date.now() + 3600_000,
      user: STAFF_USER,
      locked: false,
    });
    const open = alert({ level: 'SecondContact', secondContactAt: '2026-11-02T02:15:00Z' });
    const acknowledged = alert({
      ...open,
      acknowledgedAt: '2026-11-02T02:20:00Z',
      acknowledgedBy: 'Dr. Meera Iyer',
      minutesToAcknowledge: 20,
    });
    const cleared = alert({
      id: '0194f2a0-0000-7000-8000-00000000e102',
      code: 'clock.offset',
      message: 'The server clock is 75 s off.',
      raisedAt: '2026-10-30T10:00:00Z',
      toldAt: '2026-10-30T10:00:00Z',
      acknowledgedAt: '2026-10-30T10:03:00Z',
      acknowledgedBy: 'IT Officer',
      clearedAt: '2026-10-30T11:00:00Z',
      minutesToAcknowledge: 3,
    });
    server = fakeServer({
      [`GET ${ALERTS}`]: report([open, cleared], { acknowledged: 1, unacknowledged: 1, reachedSecondContact: 1 }),
      [`POST ${ALERTS}/${open.id}/acknowledge`]: acknowledged,
      'GET /api/v1/admin/system/alert-drills': [],
      'GET /api/v1/admin/system/health': {},
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('shows the open alert and how far it has gone, and acknowledges it', async () => {
    const user = userEvent.setup();
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Hc360ThemeProvider>
          <AlertIncidents />
        </Hc360ThemeProvider>
      </QueryClientProvider>,
    );

    expect(await screen.findByText('Not acknowledged: it has reached the second contact')).toBeInTheDocument();
    expect(screen.getByText(/1 reached the second contact/)).toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'Alerts of the last 30 days' })).toBeInTheDocument();
    expect(screen.getByText('Acknowledged by IT Officer 3 min after it was told; cleared')).toBeInTheDocument();

    expect(screen.queryByText(/that cleared are listed/)).not.toBeInTheDocument();

    await user.click(
      screen.getByRole('button', { name: 'Acknowledge the alert: No successful backup in the last 36 hours.' }),
    );
    await waitFor(() =>
      expect(server.requests.filter((r) => r.method === 'POST').map((r) => r.path)).toEqual([
        `${ALERTS}/0194f2a0-0000-7000-8000-00000000e101/acknowledge`,
      ]),
    );
  });

  it('says when the list holds only the latest of the alerts that cleared, and offers no second acknowledgement', async () => {
    const acknowledged = alert({ acknowledgedAt: '2026-11-02T02:03:00Z', acknowledgedBy: 'IT Officer' });
    const cleared = alert({
      id: '0194f2a0-0000-7000-8000-00000000e103',
      acknowledgedAt: '2026-11-01T02:05:00Z',
      clearedAt: '2026-11-01T03:00:00Z',
    });
    fakeServer({
      [`GET ${ALERTS}`]: report([acknowledged, cleared], { total: 502, acknowledged: 502, unacknowledged: 0 }),
      'GET /api/v1/admin/system/alert-drills': [],
      'GET /api/v1/admin/system/health': {},
    });
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Hc360ThemeProvider>
          <AlertIncidents />
        </Hc360ThemeProvider>
      </QueryClientProvider>,
    );

    expect(
      await screen.findByText('The latest 1 of the 501 that cleared are listed; the figures above count them all.'),
    ).toBeInTheDocument();
    expect(screen.getByText(/^502 critical alerts in the last 30 days/)).toBeInTheDocument();
    expect(screen.getByText('Acknowledged by IT Officer 3 min after it was told; still there')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Acknowledge the alert/ })).not.toBeInTheDocument();
  });
});
