import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as AdminApi from './api';
import { changeDatabasePassword, type DatabasePasswordStatus } from './api';
import { DatabasePassword } from './DatabasePassword';

// The server, the toasts and the signed-in user are outside the test; the section's own behaviour is what is tested.
const readStatus = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof AdminApi>();
  return {
    ...actual,
    changeDatabasePassword: vi.fn(),
    adminQueries: {
      ...actual.adminQueries,
      databasePassword: () => ({ queryKey: ['admin', 'database-password'], queryFn: () => readStatus() }),
    },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
const permissions = vi.hoisted(() => ({ manage: true }));
vi.mock('@/lib/permissions', () => ({ usePermission: () => permissions.manage }));

function status(patch: Partial<DatabasePasswordStatus>): DatabasePasswordStatus {
  return {
    username: 'hc360_app',
    canChange: true,
    reason: null,
    lastChangedAt: null,
    lastChangedBy: null,
    ...patch,
  };
}

function renderSection() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>
        <DatabasePassword />
      </FluentProvider>
    </QueryClientProvider>,
  );
}

describe('DatabasePassword', () => {
  beforeEach(() => {
    readStatus.mockReset();
    vi.mocked(changeDatabasePassword)
      .mockReset()
      .mockResolvedValue({ changedAt: '2026-10-07T05:00:00Z', settingsChanged: ['ConnectionStrings:Hospital'] });
    Object.values(notify).forEach((spy) => spy.mockClear());
    permissions.manage = true;
  });

  it('changes the password after asking, and says so', async () => {
    const user = userEvent.setup();
    readStatus.mockResolvedValue(status({}));
    renderSection();

    expect(await screen.findByText(/Not changed since installation/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Change password' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Change the database password?' });
    expect(dialog).toHaveTextContent('a new random password');
    await user.click(within(dialog).getByRole('button', { name: 'Change password' }));

    await waitFor(() => expect(changeDatabasePassword).toHaveBeenCalledTimes(1));
    expect(notify.success).toHaveBeenCalledWith(
      'Database password changed',
      'HC360 signs in to the database with the new password from now on.',
    );
  });

  it('says when and by whom it was last changed, and offers nothing to those who may not change it', async () => {
    permissions.manage = false;
    readStatus.mockResolvedValue(status({ lastChangedAt: '2026-09-01T05:00:00Z', lastChangedBy: 'IT Admin' }));
    renderSection();

    expect(await screen.findByText(/by IT Admin/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Change password' })).not.toBeInTheDocument();
  });

  it('says why the password cannot be changed here', async () => {
    readStatus.mockResolvedValue(
      status({ canChange: false, reason: 'The database connection is set outside the settings file.' }),
    );
    renderSection();

    expect(await screen.findByText('The database connection is set outside the settings file.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Change password' })).not.toBeInTheDocument();
  });
});
