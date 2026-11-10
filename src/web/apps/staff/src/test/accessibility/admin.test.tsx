import type * as Core from '@hc360/core';
import type * as EChartModule from '@/lib/EChart';
import { formatDateTime } from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '../appScreen';
import { DEVICES_ANSWERS, SYSTEM_HEALTH, SYSTEM_HEALTH_ANSWERS } from './fixtures/admin';

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

  it('without a rollover, says nothing of a next or previous CA', async () => {
    server = fakeServer(SYSTEM_HEALTH_ANSWERS);
    openScreen('/admin/system');

    expect(await screen.findByText('CA expires', {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByText(/^Next CA/)).not.toBeInTheDocument();
    expect(screen.queryByText('Previous CA (switched from)')).not.toBeInTheDocument();
  });
});
