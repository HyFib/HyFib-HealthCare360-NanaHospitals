import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { EnrollmentCodeRequest } from './api';

// Enrolling another administration desktop on the devices screen, whole with the shell, against a fake server: the
// hospital can be administered from a workstation, so once one is enrolled its code waits for a second administrator.
// The screen says it was asked for, and shows the code to the administrator who asked once it is approved.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const APPROVAL_ID = '0192a000-0000-7000-8000-00000000c901';

// Made for the test run, as the server makes one: never a code anyone uses.
const letters = () =>
  Array.from({ length: 4 }, () => 'ABCDEFGHJKMNPQRSTUVWXYZ23456789'[Math.floor(Math.random() * 31)]).join('');
const CODE = `${letters()}-${letters()}`;

let server: ReturnType<typeof fakeServer>;
let approved = false;

const waiting = (): EnrollmentCodeRequest[] => [
  {
    approvalId: APPROVAL_ID,
    kind: 'Workstation',
    note: 'Billing office PC 3',
    validMinutes: 30,
    ready: approved,
    requestedAt: '2026-11-08T05:00:00Z',
    expiresAt: approved ? '2026-11-08T05:40:00Z' : null,
  },
];

beforeEach(() => {
  stubBrowserApis();
  approved = false;
  let asked = false;
  server = fakeServer({
    'GET /api/v1/admin/devices': [],
    'GET /api/v1/admin/devices/desktop-update': { offeredVersion: null, releasedAt: null, problem: null },
    'GET /api/v1/admin/devices/enrollment-codes/requests': () => (asked ? waiting() : []),
    'POST /api/v1/admin/devices/enrollment-codes': () => {
      asked = true;
      return {
        id: APPROVAL_ID,
        kind: 'devices.enrollment',
        summary: 'Enrolment code for a workstation: Billing office PC 3',
      };
    },
    [`POST /api/v1/admin/devices/enrollment-codes/requests/${APPROVAL_ID}/code`]: {
      code: CODE,
      expiresAt: '2026-11-08T05:40:00Z',
    },
  });
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('enrolling another administration desktop', SCREEN_TESTS, () => {
  it('says the code was asked for, and shows it once another administrator approves it', async () => {
    const user = userEvent.setup();
    openScreen('/admin/devices');
    await user.click(await screen.findByRole('button', { name: 'Enrol a device' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: 'Enrol a device' });
    await user.type(within(dialog).getByRole('textbox', { name: 'Note' }), 'Billing office PC 3');
    await user.click(within(dialog).getByRole('button', { name: 'Create code' }));

    // Asked for: no code yet, and nothing to show until it is approved.
    expect(await within(dialog).findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    expect(within(dialog).queryByText(CODE)).not.toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Waiting for approval' })).toBeDisabled();
    await expectNoAxeViolations();

    // Approved: shown once, here, to the administrator who asked.
    approved = true;
    await user.click(within(dialog).getByRole('button', { name: 'Close' }));
    expect(await screen.findByText('Enrolment code approved', {}, SLOW)).toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Show the code' }, SLOW));

    const shown = await screen.findByRole('dialog', { name: 'Enrol a device' }, SLOW);
    expect(await within(shown).findByText(CODE)).toBeInTheDocument();
    expect(within(shown).getByText(/The code works once/)).toBeInTheDocument();
    await waitFor(() =>
      expect(server.requests.filter((r) => r.method === 'POST').map((r) => r.path)).toEqual([
        '/api/v1/admin/devices/enrollment-codes',
        `/api/v1/admin/devices/enrollment-codes/requests/${APPROVAL_ID}/code`,
      ]),
    );
  });
});
