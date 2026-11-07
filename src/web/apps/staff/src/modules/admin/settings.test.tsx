import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, json, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { SettingChangeCheck, SettingDto } from './api';
import type { IntegrationStatusDto } from './integrationsApi';

// Changing a setting on the settings screen, whole with the shell, against a fake server. Switching the second
// pathologist off waits for a second administrator: the screen asks the server first, then asks why, and sends the change
// once, with the reason, saying it was asked for, not saved. A change made at once asks for nothing. A change already
// waiting shows in the list. An integration's form (SMS) does the same for every change of the form at once.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const KEY = 'lab.separateVerifier';
const SETTING = `/api/v1/admin/settings/${KEY}`;

const setting = (more: Partial<SettingDto> = {}): SettingDto => ({
  id: '0192a000-0000-7000-8000-00000000e001',
  key: KEY,
  facilityId: null,
  value: true,
  isSecret: false,
  hasValue: true,
  description: null,
  updatedAt: '2026-11-01T05:00:00Z',
  version: 4,
  pending: null,
  ...more,
});

const WAITS: SettingChangeCheck = {
  waits: true,
  setting: 'Second pathologist for lab corrections',
  controls:
    'Whether a second pathologist verifies what one entered, and releases the correction of a report already released.',
  from: 'on',
  to: 'off',
};

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

async function openSetting(user: ReturnType<typeof userEvent.setup>) {
  openScreen('/admin/settings');
  await user.click(await screen.findByText(KEY, {}, SLOW));
  return screen.findByRole('dialog', { name: KEY }, SLOW);
}

describe('changing a setting', SCREEN_TESTS, () => {
  it('asks why before asking a second administrator to switch the second pathologist off, and says it was asked for', async () => {
    server = fakeServer({
      'GET /api/v1/admin/settings': [setting()],
      [`POST ${SETTING}/check`]: WAITS,
      [`PUT ${SETTING}`]: json(
        {
          id: '0192a000-0000-7000-8000-00000000e901',
          kind: 'settings.change',
          summary: 'Second pathologist for lab corrections: on → off',
        },
        202,
      ),
    });
    const user = userEvent.setup();
    const dialog = await openSetting(user);

    const value = within(dialog).getByRole('textbox', { name: 'Value' });
    await user.clear(value);
    await user.type(value, 'false');
    expect(within(dialog).queryByRole('textbox', { name: /Reason/ })).not.toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    // The server says it waits: the screen says what changes and asks why, and sends nothing until it is given.
    expect(await within(dialog).findByText('Waits for a second administrator')).toBeInTheDocument();
    expect(within(dialog).getByText(/Second pathologist for lab corrections: on → off/)).toBeInTheDocument();
    const ask = within(dialog).getByRole('button', { name: 'Ask for approval' });
    expect(ask).toBeDisabled();
    expect(server.requests.filter((request) => request.method === 'PUT')).toEqual([]);
    await user.type(
      within(dialog).getByRole('textbox', { name: /Reason, for the administrator who decides/ }),
      'Dr Rao is on leave this month.',
    );
    await expectNoAxeViolations();

    await user.click(ask);
    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    const puts = server.requests.filter((request) => request.method === 'PUT');
    expect(puts).toHaveLength(1);
    expect(puts[0]?.body).toMatchObject({ value: false, version: 4, reason: 'Dr Rao is on leave this month.' });
    expect(
      server.requests.filter((request) => request.method === 'POST' && request.path === `${SETTING}/check`),
    ).toHaveLength(1);
  });

  it('saves a change that does not wait at once, asking for no reason', async () => {
    server = fakeServer({
      'GET /api/v1/admin/settings': [setting({ value: false })],
      [`POST ${SETTING}/check`]: { ...WAITS, waits: false, from: 'off', to: 'on' },
      [`PUT ${SETTING}`]: null,
    });
    const user = userEvent.setup();
    const dialog = await openSetting(user);

    const value = within(dialog).getByRole('textbox', { name: 'Value' });
    await user.clear(value);
    await user.type(value, 'true');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Setting saved', {}, SLOW)).toBeInTheDocument();
    const put = server.requests.find((request) => request.method === 'PUT');
    expect(put?.body).toMatchObject({ value: true, version: 4, reason: null });
  });

  it('shows a change waiting for approval in the list and on the setting', async () => {
    server = fakeServer({
      'GET /api/v1/admin/settings': [
        setting({
          pending: {
            approvalId: '0192a000-0000-7000-8000-00000000e902',
            summary: 'Second pathologist for lab corrections: on → off',
            requestedBy: 'Hospital Admin',
            requestedAt: '2026-11-06T05:00:00Z',
          },
        }),
      ],
    });
    const user = userEvent.setup();
    openScreen('/admin/settings');
    expect(
      await screen.findByText('Waiting for approval: Second pathologist for lab corrections: on → off', {}, SLOW),
    ).toBeInTheDocument();

    await user.click(screen.getByText(KEY));
    const dialog = await screen.findByRole('dialog', { name: KEY }, SLOW);
    expect(within(dialog).getByText('Waiting for approval')).toBeInTheDocument();
    expect(within(dialog).getByText(/asked for by Hospital Admin/)).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it("asks why once for an integration's form, and sends its change with the reason", async () => {
    const sms: IntegrationStatusDto = {
      name: 'sms',
      displayName: 'SMS (TRAI DLT)',
      provider: 'simulator',
      enabled: true,
      configured: true,
      health: 'healthy',
      lastSuccessAt: null,
      lastFailureAt: null,
      lastError: null,
      failuresLast24Hours: 0,
      listeners: null,
    };
    server = fakeServer({
      'GET /api/v1/admin/integrations': [sms],
      'GET /api/v1/admin/settings': [
        setting({ key: 'integrations.sms', value: { enabled: true, provider: 'simulator', senderId: 'NANAHS' } }),
      ],
      'POST /api/v1/admin/settings/integrations.sms/check': {
        waits: true,
        setting: 'Integration (integrations.sms)',
        controls: "An outside service HC360 sends patients' data or money through.",
        from: '{"enabled":true,"provider":"simulator","senderId":"NANAHS"}',
        to: '{"enabled":true,"provider":"simulator","senderId":"NANAHC"}',
      },
      'PUT /api/v1/admin/settings/integrations.sms': json(
        {
          id: '0192a000-0000-7000-8000-00000000e903',
          kind: 'settings.change',
          summary: 'Integration (integrations.sms)',
        },
        202,
      ),
    });
    const user = userEvent.setup();
    openScreen('/admin/integrations/configure/sms');

    const sender = await screen.findByRole('textbox', { name: /Sender id/ }, SLOW);
    await user.clear(sender);
    await user.type(sender, 'NANAHC');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Waits for a second administrator', {}, SLOW)).toBeInTheDocument();
    expect(server.requests.filter((request) => request.method === 'PUT')).toEqual([]);
    expect(screen.getByRole('button', { name: 'Ask for approval' })).toBeDisabled();
    await user.type(
      screen.getByRole('textbox', { name: /Reason, for the administrator who decides/ }),
      'New DLT header.',
    );
    await expectNoAxeViolations();
    await user.click(screen.getByRole('button', { name: 'Ask for approval' }));

    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    const puts = server.requests.filter((request) => request.method === 'PUT');
    expect(puts).toHaveLength(1);
    expect(puts[0]?.body).toMatchObject({ value: { senderId: 'NANAHC' }, version: 4, reason: 'New DLT header.' });
  });
});
