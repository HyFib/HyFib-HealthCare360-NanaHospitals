import type * as Core from '@hc360/core';
import type * as EChartModule from '@/lib/EChart';
import { formatDateTime } from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '../appScreen';
import { DEVICES_ANSWERS, SYSTEM_HEALTH, SYSTEM_HEALTH_ANSWERS } from './fixtures/admin';
import type { ServerNodeHealth } from '@/modules/admin/api';

// Administration screens in the frame, checked with axe-core for WCAG 2.2 A and AA problems (SRS UX-10): the devices
// page as a hospital administrator sees it in a browser while the hospital enrols its first HC360 Desktop workstation,
// and in HC360 Desktop on an enrolled one (SRS SEC-08); System health while the hospital CA is rolled over.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
// jsdom has no canvas to draw System health's WiFi chart on, and a screen whose chart fails shows its error page instead.
vi.mock('@/lib/EChart', async (importOriginal) => ({
  ...(await importOriginal<typeof EChartModule>()),
  EChart: () => null,
}));

/** In a browser while no workstation is enrolled: of administration, only enrolling one. */
const FIRST_ENROLMENT_USER = {
  ...STAFF_USER,
  permissions: ['patients.read', 'admin.devices.manage'],
  administrationNeedsDesktop: true,
};

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  server = fakeServer(DEVICES_ANSWERS);
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('devices', SCREEN_TESTS, () => {
  it('in a browser, while the first workstation is enrolled', async () => {
    openScreen('/admin/devices', FIRST_ENROLMENT_USER);
    expect(await screen.findByText('OPD queue display', {}, SLOW)).toBeInTheDocument();

    expect(screen.getByText(/Enrol the hospital's first HC360 Desktop workstation from here/)).toBeInTheDocument();
    const administration = screen.getByRole('group', { name: 'Administration' });
    expect(within(administration).getByText(/open in HC360 Desktop on a computer IT has enrolled/)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('in HC360 Desktop on an enrolled workstation', async () => {
    openScreen('/admin/devices');
    expect(await screen.findByText('OPD queue display', {}, SLOW)).toBeInTheDocument();

    expect(screen.queryByText(/first HC360 Desktop workstation/)).not.toBeInTheDocument();
    expect(screen.queryByText(/computer IT has enrolled/)).not.toBeInTheDocument();
  });
});

describe('system health', SCREEN_TESTS, () => {
  const next = '9C1D4F0B7A2E58D3C6B1F09A4E7D2C5B8A1F3E6D9C2B5A8F1E4D7C0B3A6F9E2D';
  const previous = '1E4D7C0B3A6F9E2D9C1D4F0B7A2E58D3C6B1F09A4E7D2C5B8A1F3E6D9C2B5A8F';

  it('while the next CA is prepared: that CA, which devices take before the switch', async () => {
    server = fakeServer({
      ...SYSTEM_HEALTH_ANSWERS,
      'GET /api/v1/admin/system/health': {
        ...SYSTEM_HEALTH,
        certificate: { ...SYSTEM_HEALTH.certificate, nextCaFingerprint: next },
      },
    });
    openScreen('/admin/system');

    expect(
      await screen.findByText('Next CA (prepared; devices take it before the switch)', {}, SLOW),
    ).toBeInTheDocument();
    expect(screen.getByText(next)).toBeInTheDocument();
    expect(screen.queryByText('Previous CA (switched from)')).not.toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('after the switch: the CA it replaced, and until when devices keep it', async () => {
    const until = new Date(Date.now() + 200 * 24 * 3600_000).toISOString();
    server = fakeServer({
      ...SYSTEM_HEALTH_ANSWERS,
      'GET /api/v1/admin/system/health': {
        ...SYSTEM_HEALTH,
        certificate: { ...SYSTEM_HEALTH.certificate, previousCaFingerprint: previous, previousCaExpiresAt: until },
      },
    });
    openScreen('/admin/system');

    expect(await screen.findByText('Previous CA (switched from)', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(previous)).toBeInTheDocument();
    expect(screen.getByText('Devices keep it as a second CA until')).toBeInTheDocument();
    expect(screen.getByText(formatDateTime(until))).toBeInTheDocument();
    expect(screen.queryByText(/^Next CA/)).not.toBeInTheDocument();
    await expectNoAxeViolations();
  });

  /** Several servers (SCAL-01), one silent for two days: it stays, and its alert is told every day, until it is removed. */
  const severalServers = (): ServerNodeHealth[] => {
    const node = (machineName: string, role: string, id: string, reporting: boolean): ServerNodeHealth => ({
      server: { ...SYSTEM_HEALTH.server, machineName, role, node: id },
      network: SYSTEM_HEALTH.network,
      storage: SYSTEM_HEALTH.storage,
      certificate: SYSTEM_HEALTH.certificate,
      power: SYSTEM_HEALTH.power,
      warnings: [],
      reportedAt: new Date(Date.now() - (reporting ? 30_000 : 50 * 3600_000)).toISOString(),
      reporting,
    });
    return [node('HC360-JOBS', 'worker', 'a1b2', true), node('HC360-API2', 'api', 'c3d4', false)];
  };

  it('with several servers, one who manages the server removes one that stopped reporting, once confirmed', async () => {
    server = fakeServer({
      ...SYSTEM_HEALTH_ANSWERS,
      'GET /api/v1/admin/system/health': { ...SYSTEM_HEALTH, servers: severalServers() },
      'DELETE /api/v1/admin/system/servers/c3d4': null,
    });
    openScreen('/admin/system');

    const remove = await screen.findByRole('button', { name: 'Remove HC360-API2' }, SLOW);
    expect(screen.queryByRole('button', { name: 'Remove HC360-JOBS' })).not.toBeInTheDocument();
    await expectNoAxeViolations();

    await userEvent.click(remove);
    const dialog = await screen.findByRole('alertdialog', { name: 'Remove HC360-API2?' });
    expect(within(dialog).getByText(/only if it was retired, renamed, or set up again elsewhere/)).toBeInTheDocument();
    await expectNoAxeViolations();

    await userEvent.click(within(dialog).getByRole('button', { name: 'Remove' }));
    await waitFor(() =>
      expect(server.requests.filter((r) => r.method === 'DELETE').map((r) => r.path)).toEqual([
        '/api/v1/admin/system/servers/c3d4',
      ]),
    );
  });

  it('with several servers, one who only sees System health is not offered to remove one', async () => {
    server = fakeServer({
      ...SYSTEM_HEALTH_ANSWERS,
      'GET /api/v1/admin/system/health': { ...SYSTEM_HEALTH, servers: severalServers() },
    });
    openScreen('/admin/system', { ...STAFF_USER, permissions: ['admin.system.read'] });

    expect(await screen.findByText('HC360-API2', {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Remove/ })).not.toBeInTheDocument();
  });

  it('without a rollover, says nothing of a next or previous CA', async () => {
    server = fakeServer(SYSTEM_HEALTH_ANSWERS);
    openScreen('/admin/system');

    expect(await screen.findByText('CA expires', {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByText(/^Next CA/)).not.toBeInTheDocument();
    expect(screen.queryByText('Previous CA (switched from)')).not.toBeInTheDocument();
  });
});
