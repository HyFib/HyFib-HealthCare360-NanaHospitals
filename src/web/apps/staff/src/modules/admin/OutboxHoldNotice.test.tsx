import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as HoldApi from './outboxHold';
import { heldCount, heldSince, releaseOutbox, releasedText, setByRestore, type OutboxHold } from './outboxHold';
import { OutboxHoldNotice } from './OutboxHoldNotice';

// The server, the toasts, the signed-in user and the app's router are outside the test.
vi.mock('./outboxHold', async (importOriginal) => ({
  ...(await importOriginal<typeof HoldApi>()),
  releaseOutbox: vi.fn(),
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
const permissions = vi.hoisted(() => ({ granted: new Set<string>() }));
vi.mock('@/lib/permissions', () => ({ usePermission: (permission: string) => permissions.granted.has(permission) }));
vi.mock('@tanstack/react-router', () => ({
  Link: ({ children }: { children: ReactNode }) => <a href="/admin/notification-logs?status=Queued">{children}</a>,
}));

/** As the restore sets it. */
const RESTORE: OutboxHold = {
  reason: 'restore',
  since: '2026-11-14T04:30:00+00:00',
  backupTakenAt: '2026-11-13T20:30:00+00:00',
};

function renderNotice(hold: OutboxHold = RESTORE, held = 12) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <OutboxHoldNotice hold={hold} held={held} />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('OutboxHoldNotice', () => {
  beforeEach(() => {
    vi.mocked(releaseOutbox).mockReset().mockResolvedValue({ releasedAt: '2026-11-14T05:00:00Z', held: 12 });
    Object.values(notify).forEach((spy) => spy.mockClear());
    permissions.granted = new Set(['admin.system.manage', 'admin.notifications.manage']);
  });

  it('says how many messages queued before the restore are held, and releases them after asking', async () => {
    const user = userEvent.setup();
    renderNotice();

    expect(screen.getByText(/^12 messages queued before the restore at .+ are held$/)).toBeInTheDocument();
    expect(screen.getByText(/messages queued since go as usual/)).toBeInTheDocument();
    expect(screen.getByText(/messages waiting then may have gone out since/)).toBeInTheDocument();
    // The restore's own reason is not words for anyone.
    expect(screen.queryByText(/ restore\./)).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Check the queued messages' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Release' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Release the held messages?' });
    expect(dialog).toHaveTextContent('including any that went out after the backup was made');
    await user.click(within(dialog).getByRole('button', { name: 'Release' }));

    await waitFor(() => expect(releaseOutbox).toHaveBeenCalledTimes(1));
    expect(notify.success).toHaveBeenCalledWith(
      'Outgoing messages released',
      '12 messages go now, as soon as each provider answers.',
    );
  });

  it('gives the reason of a hold set for something other than a restore', () => {
    renderNotice({ reason: 'The SMS gateway is being changed', since: RESTORE.since, backupTakenAt: null }, 1);

    expect(screen.getByText(/^1 message queued before .+ is held$/)).toBeInTheDocument();
    expect(screen.getByText(/The SMS gateway is being changed\./)).toBeInTheDocument();
  });

  it('says when the release was refused', async () => {
    const user = userEvent.setup();
    const refused = new Error('Outgoing messages are not held: there is nothing to release.');
    vi.mocked(releaseOutbox).mockRejectedValue(refused);
    renderNotice();

    await user.click(screen.getByRole('button', { name: 'Release' }));
    await user.click(
      within(await screen.findByRole('alertdialog', { name: 'Release the held messages?' })).getByRole('button', {
        name: 'Release',
      }),
    );

    await waitFor(() => expect(notify.error).toHaveBeenCalledWith(refused, 'The messages were not released'));
    expect(notify.success).not.toHaveBeenCalled();
  });

  it('offers no release to those who may not manage the server, and no link to those who may not read the log', () => {
    permissions.granted = new Set();
    renderNotice();

    expect(screen.getByText(/are held$/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Release' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
    expect(screen.getByText(/Check the queued messages in the notification log\./)).toBeInTheDocument();
  });

  it('says every message is held when the server could not read the hold', () => {
    renderNotice(
      {
        reason: 'The setting messaging.outbox.hold holds a value HC360 cannot read.',
        since: '0001-01-01T00:00:00+00:00',
        backupTakenAt: null,
      },
      3,
    );

    expect(screen.getByText('Outgoing messages are held')).toBeInTheDocument();
    expect(screen.getByText(/The hold cannot be read, so no message goes to the internet/)).toBeInTheDocument();
  });
});

describe('outbox hold texts', () => {
  it('knows since when only when the server could tell', () => {
    expect(heldSince(RESTORE)).toBe(RESTORE.since);
    expect(heldSince({ since: '0001-01-01T00:00:00+00:00' })).toBeNull();
    expect(heldSince({ since: 'not a time' })).toBeNull();
  });

  it('tells a hold the restore set from another', () => {
    expect(setByRestore(RESTORE)).toBe(true);
    expect(setByRestore({ reason: 'Restore' })).toBe(true);
    expect(setByRestore({ reason: 'The SMS gateway is being changed' })).toBe(false);
    expect(setByRestore({ reason: null })).toBe(false);
  });

  it('counts the messages held and those a release lets go', () => {
    expect([0, 1, 40].map(heldCount)).toEqual(['No message', '1 message', '40 messages']);
    expect(releasedText({ held: 0 })).toBe('No message was held.');
    expect(releasedText({ held: 1 })).toBe('1 message goes now, as soon as each provider answers.');
    expect(releasedText({ held: 40 })).toBe('40 messages go now, as soon as each provider answers.');
  });
});
