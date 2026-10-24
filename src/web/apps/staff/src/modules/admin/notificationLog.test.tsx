import type * as Core from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { AbdmGatewayStatus } from './integrationsApi';
import { abdmGatewayDisplay, consentManagerLabel } from './integrationsApi';
import type { NotificationLogDto } from './notificationTemplatesApi';
import { deliveryStatusDisplay, NOTIFICATION_STATUSES } from './notificationTemplatesApi';

// The notification delivery log (HMS-04) and the ABDM gateway on the integrations screen, whole with the shell, against
// a fake server: what they show, and the filters the log sends.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const id = (suffix: string) => `0192a000-0000-7000-8000-00000000a${suffix}`;

const log = (overrides: Partial<NotificationLogDto> & Pick<NotificationLogDto, 'id'>): NotificationLogDto => ({
  createdAt: '2026-10-23T05:00:00Z',
  channel: 'Sms',
  templateKey: 'lab.report_ready',
  recipient: '98XXXXXX10',
  patientId: id('900'),
  status: 'Delivered',
  error: null,
  sentAt: '2026-10-23T05:00:02Z',
  ...overrides,
});

const LOGS = [
  log({ id: id('001') }),
  log({
    id: id('002'),
    channel: 'WhatsApp',
    templateKey: 'appointment.reminder',
    status: 'Failed',
    error: 'Meta refused the message: template not approved (132001).',
    sentAt: null,
  }),
  log({
    id: id('003'),
    channel: 'Email',
    templateKey: 'billing.invoice',
    recipient: 'a***@example.com',
    status: 'Skipped',
    error: 'The patient has no e-mail address.',
    sentAt: null,
  }),
];

const GATEWAY: AbdmGatewayStatus = {
  enabled: true,
  configured: false,
  live: false,
  baseUrl: 'https://dev.abdm.gov.in/api/hiecm',
  hipId: null,
  cmId: 'sbx',
  hasClientId: true,
  hasClientSecret: false,
  problems: ['No HIP id ("hipId" in abdm.gateway): the hospital\'s HFR id registered as a HIP.'],
  remaining: ['Build the gateway client and the callback endpoints the gateway calls.'],
};

let server: ReturnType<typeof fakeServer>;

function serve(abdm: boolean) {
  server = fakeServer({
    'GET /api/v1/settings/public': { 'features.abdm': abdm },
    'GET /api/v1/admin/notification-logs': paged(LOGS),
    'GET /api/v1/admin/integrations': [],
    'GET /api/v1/abdm/gateway': GATEWAY,
  });
}

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  serve(true);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** The query strings the log was asked with, in order. */
const logQueries = () =>
  server.requests
    .filter((request) => request.method === 'GET' && request.path === '/api/v1/admin/notification-logs')
    .map((request) => Object.fromEntries(request.url.searchParams));

describe('delivery and gateway wording', () => {
  it('names every delivery status, failures in red', () => {
    expect(NOTIFICATION_STATUSES.map((status) => deliveryStatusDisplay(status).label)).toEqual([
      'Waiting',
      'Sent',
      'Delivered',
      'Failed',
      'Skipped',
      'Suppressed',
    ]);
    expect(deliveryStatusDisplay('Failed').tone).toBe('danger');
    expect(deliveryStatusDisplay('Delivered').tone).toBe('success');
  });

  it('says whether the ABDM gateway is off, not set up, or set up but not sending yet', () => {
    expect(abdmGatewayDisplay({ ...GATEWAY, enabled: false }).label).toBe('Off');
    expect(abdmGatewayDisplay(GATEWAY).label).toBe('Not set up');
    expect(abdmGatewayDisplay({ ...GATEWAY, configured: true, problems: [] }).label).toBe('Set up, not sending yet');
    expect(abdmGatewayDisplay({ ...GATEWAY, configured: true, live: true }).label).toBe('Sending records');
    expect(consentManagerLabel('sbx')).toBe('Sandbox (sbx)');
    expect(consentManagerLabel('abdm')).toBe('Production (abdm)');
    expect(consentManagerLabel(null)).toBe('Not set');
  });
});

describe('notification delivery log and ABDM gateway screens', SCREEN_TESTS, () => {
  it('lists the messages with their status, and why one failed or was skipped', async () => {
    openScreen('/admin/notification-logs');
    const table = await screen.findByRole('table', { name: 'Notification delivery log' }, SLOW);
    expect(await within(table).findByText('lab.report_ready', {}, SLOW)).toBeInTheDocument();
    // The server masks the recipient; the screen shows it as it came.
    expect(within(table).getByText('a***@example.com')).toBeInTheDocument();
    expect(within(table).getByText('Delivered')).toBeInTheDocument();
    expect(within(table).getByText('Failed')).toBeInTheDocument();
    expect(within(table).getByText('Meta refused the message: template not approved (132001).')).toBeInTheDocument();
    expect(within(table).getByText('The patient has no e-mail address.')).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Delivery log', selected: true })).toBeInTheDocument();
  });

  it('asks the server for the channel, status and message key chosen', async () => {
    const user = userEvent.setup();
    openScreen('/admin/notification-logs');
    await screen.findByRole('table', { name: 'Notification delivery log' }, SLOW);
    await user.selectOptions(screen.getByRole('combobox', { name: 'Channel' }), 'WhatsApp');
    await user.selectOptions(screen.getByRole('combobox', { name: 'Status' }), 'Failed');
    await user.type(screen.getByRole('searchbox', { name: 'Message key' }), 'Appointment');
    await waitFor(
      () =>
        expect(logQueries().at(-1)).toEqual({
          channel: 'WhatsApp',
          status: 'Failed',
          // Keys are lower-case and the server matches them as typed.
          search: 'appointment',
          page: '1',
          pageSize: '50',
        }),
      SLOW,
    );
  });

  it('shows the ABDM gateway set-up next to the other integrations, never the secret', async () => {
    openScreen('/admin/integrations');
    const gateway = (await screen.findByRole('heading', { name: 'ABDM gateway' }, SLOW)).closest('section')!;
    const section = within(gateway as HTMLElement);
    expect(await section.findByText('Not set up', {}, SLOW)).toBeInTheDocument();
    expect(section.getByText('https://dev.abdm.gov.in/api/hiecm')).toBeInTheDocument();
    expect(section.getByText('Sandbox (sbx)')).toBeInTheDocument();
    expect(section.getByText('Client secret missing')).toBeInTheDocument();
    expect(section.getByText(/No HIP id/)).toBeInTheDocument();
    expect(section.getByText(/Build the gateway client/)).toBeInTheDocument();
  });

  it('does not ask for the ABDM gateway while ABDM health records are switched off', async () => {
    serve(false);
    openScreen('/admin/integrations');
    await screen.findByRole('table', { name: 'Integrations' }, SLOW);
    expect(screen.queryByRole('heading', { name: 'ABDM gateway' })).not.toBeInTheDocument();
    expect(server.requests.some((request) => request.path === '/api/v1/abdm/gateway')).toBe(false);
  });
});
